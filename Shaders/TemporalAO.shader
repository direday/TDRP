Shader "TDRP/TemporalAO"
{
    // Temporal resolve for SSAO. Blends this frame's noisy AO (_ScreenSpaceAO, R = occlusion,
    // G = eye depth) with a reprojected history buffer, so a cheap few-sample AO converges to a
    // clean result over a handful of frames — the same trick the few-tap PCSS uses. Output keeps
    // the R = accumulated AO, G = current eye depth layout (G lets next frame validate its
    // reprojection against this surface). Reprojection follows the motion vector buffer when it's
    // available (_TemporalMotionParams.x) so moving geometry samples the correct history; otherwise
    // it falls back to camera-only reprojection. The neighborhood clamp guards both paths.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "TemporalAO"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURE2D(_CameraDepthTexture); // current device depth (global, from the prepass)
            TEXTURE2D(_ScreenSpaceAO);      // this frame's AO (global, R = occlusion, G = eye depth)
            TEXTURE2D(_AoHistory);          // last frame's resolved (R = AO, G = eye depth); on the material
            TEXTURE2D(_CameraMotionVectorsTexture); // screen-space velocity (global, from MotionVectorPass)
            SAMPLER(sampler_point_clamp);
            SAMPLER(sampler_linear_clamp);

            float4x4 _ScreenToWorld; // current inverse view-projection (depth -> world)
            float4x4 _CurrView;      // current view (to store this frame's eye depth)
            float4x4 _PrevView;      // last frame's view (for the disocclusion test)
            float4x4 _PrevViewProj;  // last frame's view-projection (to reproject into its screen)
            float4 _TemporalScreenSize; // xy: 1/size (one texel uv), zw: size (pixels)
            float4 _TemporalParams;     // x: alpha (current weight), y: depth-reject (relative),
                                        // z: hasHistory (0/1), w: neighborhood-clamp (0/1)
            float4 _TemporalMotionParams; // x: useMotionVectors (0/1)

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

            float CurrentAO(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_ScreenSpaceAO, sampler_point_clamp, uv, 0).r;
            }

            float2 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float ao = CurrentAO(uv);
                float deviceDepth = SAMPLE_TEXTURE2D_LOD(_CameraDepthTexture, sampler_point_clamp, uv, 0).r;

            #if UNITY_REVERSED_Z
                bool background = deviceDepth <= 0.0;
            #else
                bool background = deviceDepth >= 1.0;
            #endif

                float3 worldPos = ComputeWorldSpacePosition(uv, deviceDepth, _ScreenToWorld);
                float currEye = -mul(_CurrView, float4(worldPos, 1.0)).z;

                // No usable history (first frame / resize) or no geometry here -> pass current through.
                if (_TemporalParams.z < 0.5 || background)
                    return float2(ao, currEye);

                // Find where THIS pixel's surface was on screen last frame.
                float2 prevUV;
                if (_TemporalMotionParams.x > 0.5)
                {
                    // Motion-vector reprojection: follows BOTH camera and per-object motion, so a
                    // moving object samples the history at its actual previous location instead of
                    // where a static-world assumption would put it (the source of moving-geo ghosting).
                    // Convention: motion = currUV - prevUV  ->  prevUV = uv - motion. The buffer is
                    // full-res; sampling at this (possibly half-res) uv resolves correctly.
                    float2 motion = SAMPLE_TEXTURE2D_LOD(_CameraMotionVectorsTexture, sampler_point_clamp, uv, 0).xy;
                    prevUV = uv - motion;
                }
                else
                {
                    // Fallback (motion vectors off): camera-only reprojection through last frame's VP.
                    float4 prevClip = mul(_PrevViewProj, float4(worldPos, 1.0));
                    float2 prevNdc = prevClip.xy / prevClip.w;
                #if UNITY_UV_STARTS_AT_TOP
                    // Mirror the Y-flip ComputeWorldSpacePosition baked in, so reprojected UVs land on
                    // the texels the history was written at (else history is sampled mirrored).
                    prevNdc.y = -prevNdc.y;
                #endif
                    prevUV = prevNdc * 0.5 + 0.5;
                }

                if (any(prevUV < 0.0) || any(prevUV > 1.0))
                    return float2(ao, currEye);

                float2 hist = SAMPLE_TEXTURE2D_LOD(_AoHistory, sampler_linear_clamp, prevUV, 0).rg;
                float histAO = hist.r;
                float histEye = hist.g;

                // Disocclusion test: this world point's depth in LAST frame's view should match the
                // eye depth stored at the texel we reprojected to. A mismatch means a different
                // surface occupied that texel last frame -> reject the history.
                float prevEye = -mul(_PrevView, float4(worldPos, 1.0)).z;
                bool disocclude = abs(prevEye - histEye) > _TemporalParams.y * max(prevEye, 1e-3);

                // Neighborhood clamp — pull history into the min/max of the CURRENT 3x3 AO, so a
                // moving occlusion edge clamps stale history toward the new value instead of smearing.
                if (_TemporalParams.w > 0.5)
                {
                    float mn = ao, mx = ao;
                    [unroll] for (int y = -1; y <= 1; y++)
                    {
                        [unroll] for (int x = -1; x <= 1; x++)
                        {
                            float s = CurrentAO(uv + float2(x, y) * _TemporalScreenSize.xy);
                            mn = min(mn, s);
                            mx = max(mx, s);
                        }
                    }
                    histAO = clamp(histAO, mn, mx);
                }

                // Adaptive response: where the current AO STILL disagrees with the (clamped) history
                // — a moving occluder sweeping across a static surface, which the receiver's motion
                // vector can't capture — bias the blend toward the current frame proportionally to the
                // disagreement, so it snaps instead of trailing. _TemporalMotionParams.y scales how
                // hard that pushes (SsaoTemporalResponse); 0 = plain SsaoTemporalAlpha everywhere.
                float baseAlpha = _TemporalParams.x;
                float change = abs(ao - histAO);
                float adaptiveAlpha = saturate(baseAlpha + change * _TemporalMotionParams.y);

                // On disocclusion, take the current frame outright (alpha = 1) — no valid history.
                float alpha = disocclude ? 1.0 : adaptiveAlpha;
                float resolved = lerp(histAO, ao, alpha);
                return float2(resolved, currEye);
            }
            ENDHLSL
        }
    }
}
