#ifndef TDRP_SHADOWS_INCLUDED
#define TDRP_SHADOWS_INCLUDED

// The main directional shadow map. TEXTURE2D_SHADOW + SAMPLER_CMP bind it to a
// hardware comparison sampler: the GPU compares the receiver's light-space depth
// against the stored caster depth and returns 0/1 (with free 2x2 bilinear PCF).
// The name "sampler_linear_clamp_compare" is recognised by the SRP core shader
// library as an inline comparison sampler state, so we don't author a sampler asset.
TEXTURE2D_SHADOW(_MainLightShadowmap);
SAMPLER_CMP(sampler_linear_clamp_compare);

// Set by SetupLightingPass.
float4x4 _MainLightShadowMatrix; // world -> shadow-map UV (xy) + compare depth (z)
float4 _MainLightShadowParams;   // x: strength, y: depth bias (world), z: normal bias (world), w: 1/res

// Screen-space shadow mask. When the pipeline resolves the main light's shadow into a
// full-screen buffer (ScreenSpaceShadowPass), the lit shader samples that mask by pixel
// coordinate instead of filtering the shadow map inline. The mask already stores the final
// attenuation (shadow strength folded in). Only the lit shader compiles this path; the mask-
// generating shader does NOT define _SCREEN_SPACE_SHADOWS, so it filters the map for real.
#if defined(_SCREEN_SPACE_SHADOWS)
TEXTURE2D(_ScreenShadowMask);
float SampleScreenSpaceShadow(float2 svPosition)
{
	return LOAD_TEXTURE2D(_ScreenShadowMask, int2(svPosition)).r;
}
#endif

// The soft-filter keywords share one multi_compile slot. PCSS-with-pyramid is a superset of
// plain PCSS, so fold both into TDRP_PCSS and treat the pyramid as an extra capability.
#if defined(_SHADOWS_PCSS) || defined(_SHADOWS_PCSS_PYRAMID)
	#define TDRP_PCSS
#endif

#if defined(TDRP_PCSS)
// A 16-tap Poisson disk in [-1,1], rotated per-pixel so a large kernel doesn't reveal the
// fixed pattern as banding.
static const float2 kPoissonDisk16[16] =
{
	float2(-0.94201624, -0.39906216), float2( 0.94558609, -0.76890725),
	float2(-0.09418410, -0.92938870), float2( 0.34495938,  0.29387760),
	float2(-0.91588581,  0.45771432), float2(-0.81544232, -0.87912464),
	float2(-0.38277543,  0.27676845), float2( 0.97484398,  0.75648379),
	float2( 0.44323325, -0.97511554), float2( 0.53742981, -0.47373420),
	float2(-0.26496911, -0.41893023), float2( 0.79197514,  0.19090188),
	float2(-0.24188840,  0.99706507), float2(-0.81409955,  0.91437590),
	float2( 0.19984126,  0.78641367), float2( 0.14383161, -0.14100790)
};

// Cheap per-pixel hash in [0,1) (Jimenez "Next Generation Post Processing"), used to give
// each pixel a different disk rotation so undersampling reads as noise, not structured bands.
float ShadowRotationNoise(float2 pixel)
{
	const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
	return frac(magic.z * frac(dot(pixel, magic.xy)));
}
#endif

#if defined(TDRP_PCSS)
// PCSS reads the shadow map's raw depth (NOT through the comparison sampler) during the
// blocker search, so it can average occluder depths. The same physical texture can be
// bound to a normal sampler too; "sampler_point_clamp" is an inline state the SRP core
// library recognises, so no sampler asset is needed.
SAMPLER(sampler_point_clamp);
// x: blocker-search radius (shadow UV), y: penumbra scale (UV per unit [0,1] depth diff),
// z: min filter radius (shadow UV), w: unused.
float4 _MainLightShadowPcssParams;

// Per-frame rotation offset added on top of the per-pixel disk-rotation hash. The screen-space
// shadow pass varies this each frame so temporal accumulation sees a DIFFERENT noise pattern to
// average. Stays 0 on the inline path (only the SS pass sets it), i.e. stable noise when there's
// no temporal accumulation behind it.
float _ShadowRotationJitter;

// Sample taps per frame for the blocker search AND the filter (<=16, the Poisson table size).
// Fewer taps = cheaper but noisier per frame; with temporal accumulation behind it that noise
// averages out over frames, so this is the quality/perf knob. 0 (unset — e.g. the inline path
// transparents use) means "use all 16".
int _PcssTapCount;

// Raw (non-comparison) shadow depth fetch for the blocker search.
float SampleShadowDepth(float2 uv)
{
	return SAMPLE_TEXTURE2D_LOD(_MainLightShadowmap, sampler_point_clamp, uv, 0).r;
}

#if defined(_SHADOWS_PCSS_PYRAMID)
// Coarse (min, max)-depth level over the shadow map, one texel per ShadowPyramidBlock^2 region.
Texture2D<float2> _MainLightShadowMinMax;
float4 _MainLightShadowMinMaxParams; // x: coarse resolution

// HiZ-style early-out for the blocker search. Returns 1 if the whole search window is
// provably lit, 0 if provably shadowed, or -1 (mixed) when the per-pixel search is needed.
// Conservative + lossless: it only fires where every texel in the window agrees, which never
// happens at a shadow edge (those windows straddle the gradient and fall through to -1).
float QueryShadowPyramid(float2 uv, float searchRadius, float receiver)
{
	float coarseRes = _MainLightShadowMinMaxParams.x;
	// Coarse texels spanning the [uv +/- searchRadius] window. Because the block is sized to
	// 2x the max search radius, the window covers at most two coarse texels per axis.
	int2 lo = (int2)floor((uv - searchRadius) * coarseRes);
	int2 hi = (int2)floor((uv + searchRadius) * coarseRes);
	lo = clamp(lo, 0, (int)coarseRes - 1);
	hi = clamp(hi, 0, (int)coarseRes - 1);

	float regionMin = 1.0;
	float regionMax = 0.0;
	[unroll] for (int y = 0; y <= 1; y++)
	{
		[unroll] for (int x = 0; x <= 1; x++)
		{
			int2 c = min(lo + int2(x, y), hi);
			float2 mm = _MainLightShadowMinMax.Load(int3(c, 0));
			regionMin = min(regionMin, mm.x);
			regionMax = max(regionMax, mm.y);
		}
	}

	// "Lit" is inclusive at the boundary (a texel at exactly the receiver depth doesn't occlude
	// it); "shadowed" must be strict (a texel at the receiver depth IS lit). Mirrors the
	// blocker test's reverse-Z handling so the early-out matches the full search exactly.
#if UNITY_REVERSED_Z
	if (receiver >= regionMax) return 1.0; // nothing closer to the light -> lit
	if (receiver <  regionMin) return 0.0; // everything closer -> shadowed
#else
	if (receiver <= regionMin) return 1.0;
	if (receiver >  regionMax) return 0.0;
#endif
	return -1.0;
}
#endif

// PCSS: estimate the penumbra width per pixel from how far the average occluder is from the
// receiver, then run a variable-radius PCF. coord = (UV.xy, compare depth.z). svPosition is
// SV_Position.xy, used only to vary the sample-disk rotation.
float SampleMainShadowPCSS(float3 coord, float2 svPosition)
{
	float searchRadius = _MainLightShadowPcssParams.x;
	float penumbraScale = _MainLightShadowPcssParams.y;
	float minRadius = _MainLightShadowPcssParams.z;
	float receiver = coord.z;

	float s, c;
	// Seed the per-pixel rotation hash with the per-frame jitter so EACH pixel gets a fresh,
	// uncorrelated rotation every frame (a uniform global offset barely changes a 16-tap PCF
	// average — too little for temporal accumulation to average out). Jitter 0 => unchanged.
	sincos(ShadowRotationNoise(svPosition + _ShadowRotationJitter * 1000.0) * TWO_PI, s, c);
	float2x2 rot = float2x2(c, -s, s, c);

	// Per-frame tap budget + a per-frame rotation of WHICH Poisson points we pick, so successive
	// frames sample different subsets of the disk (the temporal average then covers the full set).
	// Both fall back to the full 16-tap disk when unset (inline path, no temporal behind it).
	int tapCount = _PcssTapCount > 0 ? min(_PcssTapCount, 16) : 16;
	int tapOffset = (int)(_ShadowRotationJitter * 16.0);

#if defined(_SHADOWS_PCSS_PYRAMID)
	// Stage 0 — coarse early-out. If the whole search window is provably lit or shadowed, skip
	// the per-pixel search entirely. Returns -1 (mixed) at shadow edges, where we fall through.
	float pyramid = QueryShadowPyramid(coord.xy, searchRadius, receiver);
	if (pyramid >= 0.0)
		return pyramid;
#endif

	// Stage 1 — blocker search: average the depth of texels that lie between the light and
	// the receiver (i.e. are closer to the light). On reverse-Z, "closer" means a LARGER
	// stored depth, so the comparison flips.
	float blockerSum = 0.0;
	float blockerCount = 0.0;
	for (int i = 0; i < tapCount; i++)
	{
		float2 offset = mul(rot, kPoissonDisk16[(i + tapOffset) & 15]) * searchRadius;
		float d = SampleShadowDepth(coord.xy + offset);
#if UNITY_REVERSED_Z
		if (d > receiver) { blockerSum += d; blockerCount += 1.0; }
#else
		if (d < receiver) { blockerSum += d; blockerCount += 1.0; }
#endif
	}

	// No occluder anywhere in the search region => fully lit. This is the common case over
	// open ground, and skipping stages 2-3 here is the cheap early-out.
	if (blockerCount == 0.0)
		return 1.0;

	// Stage 2 — penumbra estimate. Width grows with the receiver<->blocker depth gap; the CPU
	// folded tan(sunHalfAngle) and the ortho depth/lateral spans into penumbraScale. Clamp to
	// [minRadius, searchRadius] so contact stays crisp and we never filter past what we searched.
	float avgBlocker = blockerSum / blockerCount;
	float filterRadius = clamp(abs(receiver - avgBlocker) * penumbraScale, minRadius, searchRadius);

	// Stage 3 — variable-radius PCF through the hardware comparison sampler.
	float sum = 0.0;
	for (int j = 0; j < tapCount; j++)
	{
		float2 offset = mul(rot, kPoissonDisk16[(j + tapOffset) & 15]) * filterRadius;
		sum += SAMPLE_TEXTURE2D_SHADOW(_MainLightShadowmap, sampler_linear_clamp_compare,
			float3(coord.xy + offset, receiver));
	}
	return sum / float(tapCount);
}
#endif

// Samples the shadow map at `coord` (UV in xy, compare depth in z). Hard = one tap
// (which is itself a 2x2 hardware-PCF). Soft = a 3x3 grid of those taps, averaged,
// which softens the binary edge into a penumbra gradient.
float SampleMainShadowPCF(float3 coord)
{
#if defined(_SHADOWS_SOFT)
	float texel = _MainLightShadowParams.w; // UV size of one shadow texel (1/resolution)
	float sum = 0.0;
	[unroll] for (int y = -1; y <= 1; ++y)
	{
		[unroll] for (int x = -1; x <= 1; ++x)
		{
			float2 uv = coord.xy + float2(x, y) * texel;
			sum += SAMPLE_TEXTURE2D_SHADOW(_MainLightShadowmap, sampler_linear_clamp_compare, float3(uv, coord.z));
		}
	}
	return sum * (1.0 / 9.0);
#else
	return SAMPLE_TEXTURE2D_SHADOW(_MainLightShadowmap, sampler_linear_clamp_compare, coord);
#endif
}

// Returns light attenuation in [0,1]: 1 = fully lit, (1 - strength) = fully shadowed.
// lightDir is the surface->light direction (world). svPosition is SV_Position.xy, used
// only by the PCSS path to vary the sample-disk rotation per pixel.
float MainLightShadowAttenuation(float3 positionWS, float3 normalWS, float3 lightDir, float2 svPosition)
{
#if defined(_MAIN_LIGHT_SHADOWS)
#if defined(_SCREEN_SPACE_SHADOWS)
	// The main shadow was resolved into a full-screen mask this frame — just read it
	// (strength already folded in). positionWS / normalWS / lightDir are unused here.
	return SampleScreenSpaceShadow(svPosition);
#else
	// Bias in world space (offsets pre-scaled by the shadow texel's world size on the
	// CPU): nudge the sample along the surface normal and toward the light so a surface
	// doesn't shadow itself. Doing it in world space keeps it independent of the depth
	// convention (no reverse-Z juggling) and unifies normal + depth bias.
	positionWS += normalWS * _MainLightShadowParams.z;
	positionWS += lightDir * _MainLightShadowParams.y;

	float4 coord = mul(_MainLightShadowMatrix, float4(positionWS, 1.0));
	// Orthographic projection keeps w = 1, but divide anyway to stay correct if the
	// projection ever becomes perspective (spot lights in 3d).
	coord.xyz /= coord.w;

	// Outside the shadow frustum (beyond shadow distance, or off to the side) there is
	// no caster data, so treat those pixels as fully lit rather than clamping to an edge.
	if (any(coord.xy < 0.0) || any(coord.xy > 1.0))
		return 1.0;

#if defined(TDRP_PCSS)
	float shadow = SampleMainShadowPCSS(coord.xyz, svPosition);
#else
	float shadow = SampleMainShadowPCF(coord.xyz);
#endif
	return lerp(1.0, shadow, _MainLightShadowParams.x);
#endif
#else
	return 1.0;
#endif
}

#endif
