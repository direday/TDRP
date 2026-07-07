#ifndef TDRP_IBL_INCLUDED
#define TDRP_IBL_INCLUDED

// Core SH evaluation (SHEvalLinearL0L1 / L2 / SampleSH9). Safe to include after TDRPCore.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SphericalHarmonics.hlsl"

// Global image-based lighting: the indirect specular (split-sum) term, and later the SH-L2 diffuse
// irradiance. Compiled in only when the pipeline sets _ENV_IBL (env resources baked + toggle on), so
// the fallback hemisphere-ambient path never samples unbound IBL resources. Bound by
// EnvironmentIBLPass each frame.

// GGX-prefiltered environment: mip 0 = mirror, higher mips = rougher. The mip chain is LINEAR in
// perceptual roughness (that's how PrefilterEnv baked it), so we index it linearly — not via
// PerceptualRoughnessToMipmapLevel, which assumes a different mip distribution.
TEXTURECUBE(_PrefilteredEnv);
SAMPLER(sampler_PrefilteredEnv);
// Pre-integrated environment BRDF: rg = (F0 scale, bias), indexed by (NdotV, perceptualRoughness).
TEXTURE2D(_EnvBrdfLut);
SAMPLER(sampler_EnvBrdfLut);

float _EnvMaxMip;    // highest prefiltered mip index (roughness 1)
float _EnvIntensity; // overall indirect multiplier

// SH-L2 diffuse irradiance of the environment, from RenderSettings.ambientProbe, packed CPU-side in
// the standard Unity shAr..shC layout (SetupLightingPass / EnvironmentIBLPass). SampleSH9 = the diffuse
// (cosine-convolved) ambient, exactly as URP's SampleSH.
float4 _TDRPSHCoefficients[7];

float3 SampleSHIrradiance(float3 normalWS)
{
	return max(0.0, SampleSH9(_TDRPSHCoefficients, normalWS));
}

// Fresnel with a roughness-aware ceiling (Sébastien Lagarde): rough surfaces shouldn't show a full
// grazing Fresnel spike. Used to split indirect energy between specular and diffuse (Step 4).
float3 FresnelSchlickRoughness(float3 f0, float cosTheta, float perceptualRoughness)
{
	float3 fMax = max((1.0 - perceptualRoughness).xxx, f0);
	return f0 + (fMax - f0) * pow(1.0 - cosTheta, 5.0);
}

// Specular occlusion from AO (Sébastien Lagarde): a smooth surface off-normal shouldn't take the
// full AO darkening a diffuse surface would, so it scales with NdotV + roughness. Prevents dark AO
// from wrongly killing sharp reflections while still occluding rough/grazing specular.
float SpecularOcclusion(float NdotV, float ao, float perceptualRoughness)
{
	return saturate(pow(NdotV + ao, exp2(-16.0 * perceptualRoughness - 1.0)) - 1.0 + ao);
}

// Split-sum indirect specular: prefiltered environment radiance in the reflection direction, scaled
// by the pre-integrated env BRDF (F0 * scale + bias) and a specular-occlusion term from AO.
float3 EvaluateIndirectSpecular(float3 f0, float3 N, float3 V, float perceptualRoughness, float ao)
{
	float NdotV = saturate(dot(N, V));
	float3 R = reflect(-V, N);

	float mip = perceptualRoughness * _EnvMaxMip;
	float3 prefiltered = SAMPLE_TEXTURECUBE_LOD(_PrefilteredEnv, sampler_PrefilteredEnv, R, mip).rgb;

	float2 dfg = SAMPLE_TEXTURE2D_LOD(_EnvBrdfLut, sampler_EnvBrdfLut,
		float2(NdotV, perceptualRoughness), 0).rg;

	// Single-scatter split-sum term (F0 * scale + bias).
	float3 FssEss = f0 * dfg.x + dfg.y;
	// Multi-scatter energy compensation (Fdez-Aguera 2019): the single-scatter GGX loses the energy
	// that would bounce multiple times between microfacets, darkening rough/metallic surfaces. Add that
	// lobe back from the SAME LUT terms — Ess is the total single-scatter reflectance for a white F0, so
	// Ems = 1-Ess is the missing energy; redistribute it with the average Fresnel Favg. Cheap (no extra
	// texture), and since metals have no diffuse this is what brightens them back to plausible.
	float Ess = dfg.x + dfg.y;
	float Ems = 1.0 - Ess;
	float3 Favg = f0 + (1.0 - f0) * (1.0 / 21.0);
	float3 FmsEms = (Ems * FssEss * Favg) / (1.0 - Favg * Ems);

	float specOcc = SpecularOcclusion(NdotV, ao, perceptualRoughness);
	return prefiltered * (FssEss + FmsEms) * specOcc * _EnvIntensity;
}

#endif
