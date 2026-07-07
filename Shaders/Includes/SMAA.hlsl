#ifndef TDRP_SMAA_INCLUDED
#define TDRP_SMAA_INCLUDED

// Unity/SRP adapter for the reference SMAA implementation (iryoku/smaa, vendored verbatim as
// SMAA_Reference.hlsl — MIT, see its header). The reference is written against DX/GL shading-language
// macros; we select SMAA_CUSTOM_SL and supply the texture/sampler porting macros in terms of Unity
// core's TEXTURE2D / SAMPLER / SAMPLE_TEXTURE2D* so it compiles inside the SRP shader stack.
//
// The three public entry points our passes call:
//   SMAALumaEdgeDetectionPS  (we use our own gamma edge pass instead — see Smaa.shader pass 0)
//   SMAABlendingWeightCalculationPS(texcoord, pixcoord, offset[3], edges, area, search, subsample)
//   SMAANeighborhoodBlendingPS(texcoord, offset, color, blend)
// Their companion *VS functions compute the sampling offsets; we call those from the fragment stage
// (cheap arithmetic, and simpler than threading varyings through a full-screen-triangle vertex).

#include "TDRPCore.hlsl" // Common.hlsl: TEXTURE2D, SAMPLER, SAMPLE_TEXTURE2D*, mad/saturate/lerp

// Render-target metrics (1/w, 1/h, w, h). Set on the material by the SMAA passes. Must be defined
// before including the reference — its porting macros and search loops reference it.
float4 _SmaaRTMetrics;
#define SMAA_RT_METRICS _SmaaRTMetrics

// Full quality: threshold 0.1, 16 orthogonal + 8 diagonal search steps, corner rounding 25.
#define SMAA_PRESET_HIGH
#define SMAA_CUSTOM_SL
#define SMAA_INCLUDE_VS 1
#define SMAA_INCLUDE_PS 1

// Inline sampler states (name-encoded filter/wrap, recognised by the Unity shader compiler).
SAMPLER(sampler_linear_clamp);
SAMPLER(sampler_point_clamp);

#define SMAATexture2D(tex) TEXTURE2D(tex)
#define SMAATexturePass2D(tex) tex
#define SMAASampleLevelZero(tex, coord) SAMPLE_TEXTURE2D_LOD(tex, sampler_linear_clamp, coord, 0)
#define SMAASampleLevelZeroPoint(tex, coord) SAMPLE_TEXTURE2D_LOD(tex, sampler_point_clamp, coord, 0)
#define SMAASampleLevelZeroOffset(tex, coord, offset) SAMPLE_TEXTURE2D_LOD(tex, sampler_linear_clamp, (coord) + (offset) * SMAA_RT_METRICS.xy, 0)
#define SMAASample(tex, coord) SAMPLE_TEXTURE2D(tex, sampler_linear_clamp, coord)
#define SMAASamplePoint(tex, coord) SAMPLE_TEXTURE2D(tex, sampler_point_clamp, coord)
#define SMAASampleOffset(tex, coord, offset) SAMPLE_TEXTURE2D(tex, sampler_linear_clamp, (coord) + (offset) * SMAA_RT_METRICS.xy)
#define SMAA_FLATTEN [flatten]
#define SMAA_BRANCH [branch]

// SMAASeparatePS (unused here) references these MS macros, undefined under SMAA_CUSTOM_SL. Provide
// them so the reference still compiles as a whole; the function is never instantiated.
#define SMAATexture2DMS2(tex) Texture2DMS<float4, 2> tex
#define SMAALoad(tex, pos, sample) tex.Load(pos, sample)

#include "SMAA_Reference.hlsl"

#endif // TDRP_SMAA_INCLUDED
