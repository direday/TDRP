Shader "TDRP/SsaoUpsample"
{
    // Joint bilateral upsample of the half-res AO buffer back to full resolution. For each
    // full-res pixel, gather the 2x2 half-res texels around it and weight each by its bilinear
    // footprint AND its similarity to the full-res pixel in BOTH depth and normal.
    //
    // Depth (the half-res AO stores eye depth in G) rejects taps across a silhouette — a depth
    // step the coarse grid straddles. But a convex CREASE has continuous depth and only flips the
    // normal, so the depth term can't see it: the coarse AO bleeds across, leaving ~2px stairs
    // right where a GI-lit face meets an AO-darkened one. The normal term fixes that — full-res
    // _CameraNormalsTexture (faceted, from the prepass) turns crisply at the crease, so taps whose
    // normal disagrees with the pixel's face are rejected and the AO boundary snaps to full res.
    // (Same cross-bilateral weighting as SsaoBlur, so the two stages stay consistent.)
    // Output is the full-res _ScreenSpaceAO the lit shader samples.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "SsaoUpsample"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Normals.hlsl" // _CameraNormalsTexture (full-res world normals)

            TEXTURE2D(_ScreenSpaceAO);      // half-res (R = occlusion, G = eye depth)
            TEXTURE2D(_CameraDepthTexture); // full-res device depth (global, from the prepass)
            SAMPLER(sampler_point_clamp);

            float4x4 _ScreenToWorld;    // inverse view-projection: (uv, deviceDepth) -> world
            float4x4 _AoView;           // world -> view, for the full-res eye depth
            float4 _SsaoHalfTexelSize;  // xy: 1/halfRes (one half-res texel in uv), zw: halfRes (px)

            #define NORMAL_POWER 8.0    // crease sharpness; matches SsaoBlur

            float3 SampleNormal(float2 uv)
            {
                return normalize(SAMPLE_TEXTURE2D_LOD(_CameraNormalsTexture, sampler_point_clamp, uv, 0).xyz);
            }

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            float EyeFromDepth(float2 uv, float deviceDepth)
            {
                float3 P = ComputeWorldSpacePosition(uv, deviceDepth, _ScreenToWorld);
                return -mul(_AoView, float4(P, 1.0)).z;
            }

            float Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float c = SAMPLE_TEXTURE2D_LOD(_CameraDepthTexture, sampler_point_clamp, uv, 0).r;

            #if UNITY_REVERSED_Z
                if (c <= 0.0) return 1.0; // background -> fully open
            #else
                if (c >= 1.0) return 1.0;
            #endif

                float eye0 = EyeFromDepth(uv, c);
                float3 n0 = SampleNormal(uv); // full-res normal at this pixel

                // Position within the half-res texel grid (integer = a texel centre).
                float2 coord = uv * _SsaoHalfTexelSize.zw - 0.5;
                float2 base = floor(coord);
                float2 f = coord - base;

                float sumAO = 0.0;
                float sumW = 0.0;

                [unroll]
                for (int y = 0; y <= 1; y++)
                {
                    [unroll]
                    for (int x = 0; x <= 1; x++)
                    {
                        float2 texel = base + float2(x, y) + 0.5;
                        float2 tuv = texel * _SsaoHalfTexelSize.xy;
                        float2 tap = SAMPLE_TEXTURE2D_LOD(_ScreenSpaceAO, sampler_point_clamp, tuv, 0).rg;
                        float3 nt = SampleNormal(tuv); // full-res normal at the tap's face

                        float wx = x ? f.x : (1.0 - f.x);
                        float wy = y ? f.y : (1.0 - f.y);
                        // Depth similarity relative to the full-res eye depth (eps for background).
                        float dz = (tap.g - eye0) / (eye0 * 0.05 + 1e-4);
                        // Normal similarity — rejects taps across a crease the depth term can't see.
                        float wNormal = pow(saturate(dot(n0, nt)), NORMAL_POWER);
                        float w = wx * wy * exp(-dz * dz) * wNormal;

                        sumAO += tap.r * w;
                        sumW += w;
                    }
                }

                // All four taps rejected (a thin full-res feature with no matching half-res depth):
                // fall back to the nearest half-res sample rather than divide by ~0.
                if (sumW < 1e-4)
                    return SAMPLE_TEXTURE2D_LOD(_ScreenSpaceAO, sampler_point_clamp, uv, 0).r;

                return sumAO / sumW;
            }
            ENDHLSL
        }
    }
}
