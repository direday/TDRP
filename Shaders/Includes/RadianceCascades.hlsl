#ifndef TDRP_RADIANCE_CASCADES_INCLUDED
#define TDRP_RADIANCE_CASCADES_INCLUDED

// Consumer-side API for screen-space Radiance Cascades diffuse GI. The RC gather pass writes a
// full-resolution per-pixel diffuse irradiance into _RcIrradiance and publishes it as a global; the
// lit shader samples it here to replace the flat SH/hemisphere ambient with the spatially-varying,
// emissive-reactive RC estimate. Compiled into shading only under _SCREEN_SPACE_RC (set by the
// pipeline when RC is on AND a previous-frame color history exists), so the non-RC path never touches
// an unbound buffer — same pattern as _SCREEN_SPACE_AO.

TEXTURE2D(_RcIrradiance);

// Full-res buffer sharing the offscreen RT's pixel grid, so SV_Position indexes it directly (texel
// fetch, no sampler) — exactly like SampleScreenSpaceAO.
float3 SampleRcIrradiance(float2 svPosition)
{
#if defined(_SCREEN_SPACE_RC)
	return LOAD_TEXTURE2D(_RcIrradiance, uint2(svPosition)).rgb;
#else
	return float3(0.0, 0.0, 0.0); // caller guards with the keyword; keeps the symbol defined
#endif
}

#endif
