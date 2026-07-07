#ifndef TDRP_NORMALS_INCLUDED
#define TDRP_NORMALS_INCLUDED

// Shared scene-normal substrate.
//
// The CameraNormals pass reconstructs a world-space normal from the depth prepass and
// writes it into _CameraNormalsTexture; downstream passes (screen-space shadows, SSAO)
// sample it here instead of each reconstructing their own from depth. Reconstructing
// from depth — rather than rendering a real normal G-buffer in the prepass — is the
// "SC2-style" choice: no per-material shader changes and no extra prepass attachment,
// at the cost of FACETED geometric normals (you get the depth buffer's surface normal,
// so no normal maps and no smooth cross-mesh interpolation). That's fine for AO
// occlusion and shadow normal-bias, which is all anything samples this for.
//
// Storage is R8G8B8A8_SNorm: each axis lands directly in [-1,1], so encode/decode is a
// no-op apart from renormalising after 8-bit quantisation. (A future optimisation would
// be octahedral RG16 — half the bandwidth — but the direct form is clearer.)

#include "Includes/TDRPCore.hlsl"

// World-space scene normals, written by the CameraNormals pass. .a is unused.
TEXTURE2D(_CameraNormalsTexture);

// SNorm already stores the signed components, so packing is identity; the read just
// renormalises to undo the small error from 8-bit quantisation.
float3 EncodeSceneNormalWS(float3 normalWS) { return normalWS; }
float3 DecodeSceneNormalWS(float3 packed)   { return normalize(packed); }

// Read the reconstructed world-space normal at an exact pixel. Consumers in a
// full-screen pass have SV_Position (pixel coords) in hand, so a texel fetch (LOAD)
// avoids needing a sampler and never blends normals across a silhouette.
float3 LoadSceneNormalWS(uint2 pixelCoord)
{
    return DecodeSceneNormalWS(LOAD_TEXTURE2D(_CameraNormalsTexture, pixelCoord).xyz);
}

#endif
