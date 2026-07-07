Shader "TDRP/RcBlur"
{
    // Separable depth/normal cross-bilateral blur for the Radiance Cascades irradiance — the spatial
    // denoiser for the per-pixel ray-fan noise. Diffuse GI is an inherently LOW-FREQUENCY signal, so a
    // fairly wide blur removes the dither with almost no downside: unlike a sharp image, there's little
    // real high-frequency detail to lose. And unlike temporal accumulation it has ZERO ghosting — the
    // right fit for a max-clarity target.
    //
    // Cross-bilateral (depth + normal weights) so the bounce never bleeds across a silhouette or a
    // crease onto an unrelated surface. Depth/normal come from the globals (_CameraDepthTexture /
    // _CameraNormalsTexture); the HDR irradiance buffer carries no companion depth, so — like
    // ShadowBlur — this reconstructs eye depth itself from the depth buffer.
    //
    // No variance gate (unlike ShadowBlur): a shadow mask has flat lit/shadowed plateaus worth keeping
    // crisp, but the GI field is smooth everywhere and noisy everywhere, so we just blur uniformly.
    // Run twice (horizontal then vertical) for an O(2N) approximation of a 2D kernel.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "RcBlur"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Normals.hlsl" // _CameraNormalsTexture

            TEXTURE2D(_RcIrradiance);       // source irradiance (RGB); read + republished each pass
            TEXTURE2D(_CameraDepthTexture); // current device depth (global, from the prepass)
            SAMPLER(sampler_point_clamp);

            float4x4 _ScreenToWorld;    // inverse view-projection (depth -> world)
            float4x4 _RcBlurView;       // view (world -> eye depth, for the depth weight)
            float4 _RcBlurDirection;    // xy: one-texel step along the blur axis (uv space)
            float4 _RcBlurParams;       // x: radius (taps per side)

            #define MAX_BLUR_RADIUS 8
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

            float3 SampleRc(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_RcIrradiance, sampler_point_clamp, uv, 0).rgb;
            }

            float3 SampleNormal(float2 uv)
            {
                return normalize(SAMPLE_TEXTURE2D_LOD(_CameraNormalsTexture, sampler_point_clamp, uv, 0).xyz);
            }

            // Eye-space depth from device depth (the irradiance buffer carries none). Same
            // reconstruction the gather / SS passes use, so the tolerance stays consistent.
            float EyeDepth(float2 uv)
            {
                float d = SAMPLE_TEXTURE2D_LOD(_CameraDepthTexture, sampler_point_clamp, uv, 0).r;
                float3 worldPos = ComputeWorldSpacePosition(uv, d, _ScreenToWorld);
                return -mul(_RcBlurView, float4(worldPos, 1.0)).z;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float3 center = SampleRc(uv);
                float eye0 = EyeDepth(uv);
                float3 n0 = SampleNormal(uv);

                // Depth weight relative to the centre's eye depth (consistent near and far). eps keeps
                // the background (eye0 == 0) finite.
                float depthScale = 1.0 / (eye0 * DEPTH_SIGMA_REL + 1e-4);
                int radius = (int)_RcBlurParams.x;
                float sigma2 = 2.0 * (radius * 0.5) * (radius * 0.5) + 1e-4;

                float sumW = 1.0;
                float3 sumC = center;

                [loop]
                for (int i = -MAX_BLUR_RADIUS; i <= MAX_BLUR_RADIUS; i++)
                {
                    if (i == 0 || abs(i) > radius)
                        continue;

                    float2 tapUV = uv + _RcBlurDirection.xy * i;
                    float3 c = SampleRc(tapUV);
                    float et = EyeDepth(tapUV);
                    float3 nt = SampleNormal(tapUV);

                    float wSpatial = exp(-(i * i) / sigma2);
                    float dz = (et - eye0) * depthScale;
                    float wDepth = exp(-dz * dz);
                    float wNormal = pow(saturate(dot(n0, nt)), NORMAL_POWER);
                    float w = wSpatial * wDepth * wNormal;

                    sumW += w;
                    sumC += c * w;
                }

                return float4(sumC / sumW, 1.0);
            }
            ENDHLSL
        }
    }
}
