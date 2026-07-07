#ifndef TDRP_LIGHTING_INCLUDED
#define TDRP_LIGHTING_INCLUDED

#include "Clustering.hlsl" // PunctualLightData + cluster constants (shared with the compute pass)
#include "Shadows.hlsl"    // main directional shadow sampling
#include "IBL.hlsl"        // global image-based lighting (split-sum specular, SH diffuse), gated _ENV_IBL
#include "RadianceCascades.hlsl" // screen-space RC diffuse GI irradiance, gated _SCREEN_SPACE_RC

// Set per-frame by SetupLightingPass via SetGlobalVector.
float4 _MainLightDirection; // xyz: surface->light direction (world), w unused
float4 _MainLightColor;     // rgb: color * intensity (linear), a unused
// Hemisphere ambient: sky (up) + ground (down) colors, linear and intensity-folded, set by
// SetupLightingPass. Blended by the surface normal in SampleAmbient.
float4 _AmbientSkyColor;
float4 _AmbientGroundColor;

StructuredBuffer<PunctualLightData> _PunctualLights;
int _PunctualLightCount;

// Forward+ per-cluster light lists, produced by ClusterCull.compute and bound as
// globals by ClusterLightCullPass. We only READ them here, so they're plain
// StructuredBuffers. They are declared on the consumer side (not in the shared
// Clustering.hlsl) on purpose: the compute shader declares the same names as
// RWStructuredBuffer, and a read-only redeclaration there would clash.
StructuredBuffer<uint> _ClusterLightGrid;    // [CLUSTER_COUNT] -> light count per cluster
StructuredBuffer<uint> _ClusterLightIndices; // [CLUSTER_COUNT * MAX_LIGHTS_PER_CLUSTER]
float4 _ClusterScreenDimensions;             // xy: render-target size in pixels
float4 _ClusterNearFar;                      // x: near plane, y: far plane (positive)

// Screen-space ambient occlusion (R = occlusion), published by the SSAO pass. Sampled only in
// the opaque variant that compiles _SCREEN_SPACE_AO; transparents don't declare that keyword
// (they aren't in the depth/normal buffer the AO is built from) so they fall back to no AO.
TEXTURE2D(_ScreenSpaceAO);

// Directional light cookie: a tiling texture draped straight down over the world (XZ plane) that
// modulates the main light's color — e.g. drifting cloud shadows or a dappled canopy for a
// top-down scene. Compiled in only when SetupLightingPass sets _MAIN_LIGHT_COOKIE (the toggle is
// on AND a cookie texture is assigned), so the unlit path never samples an unbound texture.
#if defined(_MAIN_LIGHT_COOKIE)
TEXTURE2D(_MainLightCookie);
// Inline "repeat" sampler state (recognised by the SRP core shader library, so no sampler asset
// is authored). Forcing Repeat here means the cookie tiles regardless of the texture's own wrap
// import setting — one less footgun when assigning a cookie.
SAMPLER(sampler_linear_repeat);
float4 _MainLightCookieScaleOffset; // xy: world->UV scale (1/tiling), zw: scroll offset (UV)
float4 _MainLightCookieParams;      // x: strength (0 = off, 1 = full cookie), yzw: unused

// A directional light has parallel rays, so its cookie is an ORTHOGRAPHIC projection. For a
// top-down game we project straight down: the cookie is a flat horizontal layer, sampled by the
// surface's world XZ. This is world-anchored (independent of camera and sun angle), so the
// pattern stays put as you pan/zoom — the classic RTS cloud-shadow behaviour.
float3 SampleMainLightCookie(float3 positionWS)
{
	float2 uv = positionWS.xz * _MainLightCookieScaleOffset.xy + _MainLightCookieScaleOffset.zw;
	float3 cookie = SAMPLE_TEXTURE2D(_MainLightCookie, sampler_linear_repeat, uv).rgb;
	// Strength dials the effect between "no cookie" (white) and the full sampled value.
	return lerp(float3(1.0, 1.0, 1.0), cookie, _MainLightCookieParams.x);
}
#endif

// Hemisphere ambient irradiance for a world normal: full sky looking straight up, full ground
// straight down, smooth blend through the horizon (y * 0.5 + 0.5).
float3 SampleAmbient(float3 normalWS)
{
	return lerp(_AmbientGroundColor.rgb, _AmbientSkyColor.rgb, normalWS.y * 0.5 + 0.5);
}

float SampleScreenSpaceAO(float2 svPosition)
{
#if defined(_SCREEN_SPACE_AO)
	// Per-pixel texel fetch — the AO buffer is full-res and shares this offscreen RT's pixel
	// grid, so SV_Position indexes it directly (same convention as the screen-space shadow mask).
	return LOAD_TEXTURE2D(_ScreenSpaceAO, uint2(svPosition)).r;
#else
	return 1.0;
#endif
}

float3 MultiBounceAO(float visibility, float3 albedo)
{
	float3 a =  2.0404 * albedo - 0.3324;
	float3 b = -4.7951 * albedo + 0.6417;
	float3 c =  2.7552 * albedo + 0.6903;
	return max(visibility.xxx, ((visibility * a + b) * visibility + c) * visibility);
}

struct Light
{
	float3 direction;
	float3 color;
};

Light GetMainLight()
{
	Light light;
	light.direction = normalize(_MainLightDirection.xyz);
	light.color = _MainLightColor.rgb;
	return light;
}

// Builds a Light for punctual light `index`, folding distance + spot attenuation
// into the returned color so the BRDF can treat it like any other light.
Light GetPunctualLight(int index, float3 positionWS)
{
	PunctualLightData d = _PunctualLights[index];

	float3 toLight = d.positionRange.xyz - positionWS;
	float distSqr = max(dot(toLight, toLight), 1e-4);
	float3 L = toLight * rsqrt(distSqr);

	// Physical inverse-square falloff with a smooth window so intensity reaches
	// exactly 0 at the light's range (avoids a hard popping edge). t = (dist/range)^2.
	float t = distSqr * d.positionRange.w;
	float window = saturate(1.0 - t * t);
	window *= window;
	float distanceAtten = window / distSqr;

	// Spot cone falloff. For point lights scale=0, offset=1 -> this is always 1.
	float cosAngle = dot(d.spotDirScale.xyz, L);
	float spotAtten = saturate(cosAngle * d.spotDirScale.w + d.color.w);
	spotAtten *= spotAtten;

	Light light;
	light.direction = L;
	light.color = d.color.rgb * (distanceAtten * spotAtten);
	return light;
}

struct SurfaceData
{
	float3 albedo;
	float  metallic;
	float  smoothness;
	float3 normalWS;
	float3 viewDirWS;
	float3 positionWS; // needed to compute per-light direction/attenuation
};

// Cook-Torrance specular (GGX) + Lambert diffuse for a single light.
// We follow the common real-time convention of NOT dividing diffuse by PI
// (URP does the same); the specular term is normalized.
float3 BRDF(SurfaceData s, Light light)
{
	float3 N = normalize(s.normalWS);
	float3 V = normalize(s.viewDirWS);
	float3 L = light.direction;
	float3 H = normalize(L + V);

	float NdotL = saturate(dot(N, L));
	float NdotV = saturate(dot(N, V)) + 1e-4;
	float NdotH = saturate(dot(N, H));
	float LdotH = saturate(dot(L, H));

	// Dielectrics reflect ~4% at normal incidence; metals tint their reflectance
	// with the base color and have no diffuse lobe.
	float3 f0 = lerp(0.04, s.albedo, s.metallic);
	float3 diffuseColor = s.albedo * (1.0 - s.metallic);

	float perceptualRoughness = 1.0 - s.smoothness;
	float roughness = max(perceptualRoughness * perceptualRoughness, 1e-3); // alpha
	float a2 = roughness * roughness;

	// D: GGX / Trowbridge-Reitz normal distribution.
	float denom = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
	float D = a2 / (PI * denom * denom + 1e-5);

	// V: Smith geometry (Schlick-GGX), pre-divided by 4*NdotV*NdotL.
	float k = roughness * 0.5;
	float gv = NdotV / (NdotV * (1.0 - k) + k);
	float gl = NdotL / (NdotL * (1.0 - k) + k);
	float Vis = (gv * gl) / (4.0 * NdotV * NdotL + 1e-4);

	// F: Schlick Fresnel.
	float3 F = f0 + (1.0 - f0) * pow(1.0 - LdotH, 5.0);

	float3 specular = D * Vis * F;
	float3 radiance = light.color * NdotL;

	return (diffuseColor + specular) * radiance;
}

// Find the cluster a pixel belongs to. This must exactly invert the partition that
// ClusterCull.compute built, or we'd read the wrong cluster's light list.
//   - svPosition: SV_Position.xy (window pixels). The screen-tile lookup matches the
//     debug pass, which was verified against the compute pass on this offscreen-RT
//     setup, so no y-flip is needed (see the Y-convention note in the roadmap).
//   - eyeDepth: positive view-space distance along the camera forward (-viewZ).
uint3 ComputeClusterCoord(float2 svPosition, float eyeDepth)
{
	float2 screenUV = svPosition / _ClusterScreenDimensions.xy;
	uint2 tileXY = (uint2)floor(screenUV * float2(CLUSTER_X, CLUSTER_Y));
	tileXY = min(tileXY, uint2(CLUSTER_X - 1, CLUSTER_Y - 1));

	// Invert the exponential slice eyeDepth = near * (far/near)^(cz / CLUSTER_Z).
	float near = _ClusterNearFar.x;
	float far = _ClusterNearFar.y;
	float slice = log(eyeDepth / near) / log(far / near) * CLUSTER_Z;
	uint tileZ = (uint)clamp((int)floor(slice), 0, CLUSTER_Z - 1);

	return uint3(tileXY, tileZ);
}

float3 ShadeSurface(SurfaceData s, float2 svPosition)
{
	// Main directional light, attenuated by its shadow map.
	Light mainLight = GetMainLight();
	mainLight.color *= MainLightShadowAttenuation(s.positionWS, s.normalWS, mainLight.direction, svPosition);
	// Cookie (cloud shadows / gobo) modulates the light COLOR, independent of the shadow map:
	// shadows are cast occlusion, the cookie is a projected mask on the source itself.
#if defined(_MAIN_LIGHT_COOKIE)
	mainLight.color *= SampleMainLightCookie(s.positionWS);
#endif
	float3 color = BRDF(s, mainLight);

	// Forward+: shade only the punctual lights the cull pass assigned to this pixel's
	// cluster, instead of looping every visible light. View-space depth is reconstructed
	// from positionWS with the same world->view matrix the compute pass used.
	float eyeDepth = -mul(UNITY_MATRIX_V, float4(s.positionWS, 1.0)).z;
	uint clusterIndex = ClusterLinearIndex(ComputeClusterCoord(svPosition, eyeDepth));
	uint lightCount = _ClusterLightGrid[clusterIndex];
	uint base = clusterIndex * MAX_LIGHTS_PER_CLUSTER;

	for (uint i = 0; i < lightCount; i++)
	{
		uint lightIndex = _ClusterLightIndices[base + i];
		color += BRDF(s, GetPunctualLight(lightIndex, s.positionWS));
	}

	// Ambient (indirect) term — hemisphere model, modulated by AO. AO attenuates indirect light
	// ONLY (never the direct lights above). ao = 1 when SSAO is off / on transparents.
	float ao = SampleScreenSpaceAO(svPosition);
	float3 aoColor = MultiBounceAO(ao, s.albedo);

#if defined(_ENV_IBL)
	// Global IBL indirect: SH-L2 diffuse irradiance + split-sum specular, both from the environment.
	float3 N = normalize(s.normalWS);
	float3 V = normalize(s.viewDirWS);
	float NdotV = saturate(dot(N, V));
	float3 f0 = lerp(0.04, s.albedo, s.metallic);
	float perceptualRoughness = 1.0 - s.smoothness;

	// Energy split: specular reflects kS (roughness-aware Fresnel), diffuse gets the rest and only
	// for dielectrics (metals have no diffuse). AO modulates both (specular via the same term for now).
	float3 kS = FresnelSchlickRoughness(f0, NdotV, perceptualRoughness);
	float3 kD = (1.0 - kS) * (1.0 - s.metallic);
	// Diffuse irradiance source: screen-space RC (spatially-varying, emissive-reactive) when active,
	// else the flat SH-L2 probe. RC already folds the ambient baseline into its miss rays, so it
	// REPLACES the SH term (adding would double-count the sky). Specular IBL below is untouched.
#if defined(_SCREEN_SPACE_RC)
	// No aoColor here: RC's directional gather already encodes near-field occlusion (that's WHY it
	// darkens in creases), so multiplying by SSAO on top would double-count. AO still modulates the
	// specular below and the non-RC diffuse path.
	float3 diffuseIndirect = SampleRcIrradiance(svPosition) * s.albedo * kD;
#else
	float3 diffuseIndirect = SampleSHIrradiance(N) * s.albedo * kD * aoColor;
#endif
	color += diffuseIndirect * _EnvIntensity;
	color += EvaluateIndirectSpecular(f0, N, V, perceptualRoughness, ao);
#else
	// Fallback: sky/ground hemisphere ambient (diffuse only), or the RC estimate (which uses that same
	// hemisphere ambient as its baseline, so open areas stay identical) when RC is active. RC omits the
	// aoColor multiply — it already captures near-field occlusion (see the _ENV_IBL branch above).
#if defined(_SCREEN_SPACE_RC)
	color += SampleRcIrradiance(svPosition) * s.albedo * (1.0 - s.metallic);
#else
	color += SampleAmbient(s.normalWS) * s.albedo * (1.0 - s.metallic) * aoColor;
#endif
#endif
	return color;
}

#endif
