Shader "TDRP/ShadowBlur"
{
    // Variance-modulated, separable depth/normal cross-bilateral for the screen-space shadow mask —
    // the SPATIAL counterpart to the temporal resolve. It reconstructs the few-tap PCSS penumbra by
    // smoothing across SPACE instead of across frames, so there are no motion artifacts (the reason
    // to prefer it over temporal accumulation when casters move), at the cost of a little penumbra
    // softening.
    //
    // Two things keep it from just being a dumb blur:
    //   * Depth + normal weights (cross-bilateral) reject taps on a different surface, so the shadow
    //     never bleeds across a silhouette or a crease. Depth/normal come from the globals
    //     (_CameraDepthTexture / _CameraNormalsTexture) since the R8 mask carries no companion depth.
    //   * A per-pixel VARIANCE gate: the blur only fires where the mask actually varies (the noisy
    //     penumbra). Flat fully-lit / fully-shadowed regions have ~zero variance and pass through
    //     untouched, so they stay crisp — the blur budget is spent only where the dither is.
    //
    // Limitation (honest): pure spatial variance can't tell noise from a genuine sharp edge, so a
    // clean hard shadow boundary on flat ground will soften somewhat. In the few-tap regime those
    // boundaries are noisy anyway, so this is what you want; blue-noise taps would reduce the input
    // noise and let you drop the radius if you need them crisper.
    //
    // Run twice (horizontal then vertical) for an O(2N) approximation of a 2D kernel.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "ShadowBlur"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Normals.hlsl" // _CameraNormalsTexture

            TEXTURE2D(_ScreenShadowMask);   // source mask (R = shadow); read + republished each pass
            TEXTURE2D(_CameraDepthTexture); // current device depth (global, from the prepass)
            SAMPLER(sampler_point_clamp);

            float4x4 _ScreenToWorld;      // inverse view-projection (depth -> world)
            float4x4 _CurrView;           // view (world -> eye depth, for the depth weight)
            float4 _ShadowBlurDirection;  // xy: one-texel step along the blur axis (uv space)
            float4 _ShadowBlurParams;     // x: radius (taps per side), y: variance sensitivity

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

            float SampleShadow(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_ScreenShadowMask, sampler_point_clamp, uv, 0).r;
            }

            float3 SampleNormal(float2 uv)
            {
                return normalize(SAMPLE_TEXTURE2D_LOD(_CameraNormalsTexture, sampler_point_clamp, uv, 0).xyz);
            }

            // Eye-space depth reconstructed from device depth (the R8 mask carries none). Same
            // reconstruction the SS-shadow / temporal passes use, so the tolerance stays consistent.
            float EyeDepth(float2 uv)
            {
                float d = SAMPLE_TEXTURE2D_LOD(_CameraDepthTexture, sampler_point_clamp, uv, 0).r;
                float3 worldPos = ComputeWorldSpacePosition(uv, d, _ScreenToWorld);
                return -mul(_CurrView, float4(worldPos, 1.0)).z;
            }

            float Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float center = SampleShadow(uv);
                float eye0 = EyeDepth(uv);
                float3 n0 = SampleNormal(uv);

                // Depth weighting relative to the centre's eye depth, so the tolerance is consistent
                // near and far. eps keeps the background (eye0 == 0) finite.
                float depthScale = 1.0 / (eye0 * DEPTH_SIGMA_REL + 1e-4);
                int radius = (int)_ShadowBlurParams.x;
                float sigma2 = 2.0 * (radius * 0.5) * (radius * 0.5) + 1e-4;

                // Bilateral-weighted moments (centre weight 1). The weighted mean IS the blurred
                // value; the weighted variance drives the gate below.
                float sumW = 1.0;
                float sumS = center;
                float sumSq = center * center;

                [loop]
                for (int i = -MAX_BLUR_RADIUS; i <= MAX_BLUR_RADIUS; i++)
                {
                    if (i == 0 || abs(i) > radius)
                        continue;

                    float2 tapUV = uv + _ShadowBlurDirection.xy * i;
                    float s = SampleShadow(tapUV);
                    float et = EyeDepth(tapUV);
                    float3 nt = SampleNormal(tapUV);

                    float wSpatial = exp(-(i * i) / sigma2);
                    float dz = (et - eye0) * depthScale;
                    float wDepth = exp(-dz * dz);
                    float wNormal = pow(saturate(dot(n0, nt)), NORMAL_POWER);
                    float w = wSpatial * wDepth * wNormal;

                    sumW += w;
                    sumS += s * w;
                    sumSq += s * s * w;
                }

                float mean = sumS / sumW;                         // = the bilateral-blurred value
                float variance = max(0.0, sumSq / sumW - mean * mean); // same-surface variation only

                // Gate: move toward the blurred value in proportion to how much this neighbourhood
                // varies. Flat regions (variance ~0) keep the crisp centre; noisy penumbra converges
                // to the smoothed mean. Higher sensitivity -> fires at lower variance.
                float strength = saturate(variance * _ShadowBlurParams.y);
                return lerp(center, mean, strength);
            }
            ENDHLSL
        }
    }
}
