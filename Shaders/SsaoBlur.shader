Shader "TDRP/SsaoBlur"
{
    // Separable, edge-aware (cross-bilateral) blur for the SSAO buffer. Run twice — once
    // horizontally, once vertically — to approximate a 2D Gaussian at O(2N) taps instead of
    // O(N*N). Each tap is weighted by spatial distance (Gaussian) AND surface similarity (depth
    // + normal), so the noise is smoothed but the AO does NOT bleed across silhouettes or creases.
    //
    // Source is the AO buffer itself (R = occlusion, G = eye depth, written by the SSAO pass and
    // republished as _ScreenSpaceAO after each pass). Output keeps the same R/G layout so the
    // second pass — and the later temporal resolve — can read the depth too.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "SsaoBlur"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Normals.hlsl" // _CameraNormalsTexture

            TEXTURE2D(_ScreenSpaceAO); // source AO (R = occlusion, G = eye depth)
            SAMPLER(sampler_point_clamp);

            float4 _SsaoBlurDirection; // xy: one-texel step along the blur axis (uv space)

            #define BLUR_RADIUS 4
            #define DEPTH_SIGMA_REL 0.05 // depth tolerance as a fraction of eye depth
            #define NORMAL_POWER 8.0

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

            float3 SampleNormal(float2 uv)
            {
                return normalize(SAMPLE_TEXTURE2D_LOD(_CameraNormalsTexture, sampler_point_clamp, uv, 0).xyz);
            }

            float2 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 center = SAMPLE_TEXTURE2D_LOD(_ScreenSpaceAO, sampler_point_clamp, uv, 0).rg;
                float ao0 = center.r;
                float eye0 = center.g;
                float3 n0 = SampleNormal(uv);

                // Depth weighting is relative to the centre's eye depth, so the tolerance is
                // consistent near and far. eps keeps the background (eye0 == 0) case finite.
                float depthScale = 1.0 / (eye0 * DEPTH_SIGMA_REL + 1e-4);

                float sumAO = ao0;   // centre weight is 1 (spatial=depth=normal=1)
                float sumW = 1.0;

                [unroll]
                for (int i = -BLUR_RADIUS; i <= BLUR_RADIUS; i++)
                {
                    if (i == 0)
                        continue;

                    float2 tapUV = uv + _SsaoBlurDirection.xy * i;
                    float2 tap = SAMPLE_TEXTURE2D_LOD(_ScreenSpaceAO, sampler_point_clamp, tapUV, 0).rg;
                    float3 nt = SampleNormal(tapUV);

                    // Spatial Gaussian (sigma = radius/2).
                    float wSpatial = exp(-(i * i) / (2.0 * (BLUR_RADIUS * 0.5) * (BLUR_RADIUS * 0.5)));
                    // Depth similarity — reject taps on a different surface in depth.
                    float dz = (tap.g - eye0) * depthScale;
                    float wDepth = exp(-dz * dz);
                    // Normal similarity — preserve creases where the normal turns.
                    float wNormal = pow(saturate(dot(n0, nt)), NORMAL_POWER);

                    float w = wSpatial * wDepth * wNormal;
                    sumAO += tap.r * w;
                    sumW += w;
                }

                // Keep the centre's eye depth (depth must not be blurred).
                return float2(sumAO / max(sumW, 1e-4), eye0);
            }
            ENDHLSL
        }
    }
}
