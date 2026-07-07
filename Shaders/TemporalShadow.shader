Shader "TDRP/TemporalShadow"
{
    // Temporal resolve for the screen-space shadow mask. Blends this frame's noisy shadow
    // (_SsShadowRaw) with a reprojected history buffer, so PCSS noise averages out over frames.
    // Output: R = accumulated shadow (what the lit shader samples), G = current eye depth (kept
    // so next frame can validate its reprojection against this surface).
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "TemporalShadow"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURE2D(_CameraDepthTexture); // current device depth (global, from the prepass)
            TEXTURE2D(_SsShadowRaw);        // this frame's noisy shadow (global, from the SS pass)
            TEXTURE2D(_SsShadowHistory);    // last frame's resolved (R=shadow, G=eye depth); bound on the material
            TEXTURE2D(_CameraMotionVectorsTexture); // screen-space velocity (global, from MotionVectorPass)
            SAMPLER(sampler_point_clamp);
            SAMPLER(sampler_linear_clamp);

            float4x4 _ScreenToWorld; // current inverse view-projection (depth -> world)
            float4x4 _CurrView;      // current view (to store this frame's eye depth)
            float4x4 _PrevView;      // last frame's view (for the disocclusion test)
            float4x4 _PrevViewProj;  // last frame's view-projection (to reproject into its screen)
            float4 _SsShadowScreenSize; // xy: 1/size (one texel uv), zw: size (pixels)
            float4 _TemporalParams;     // x: alpha (current weight), y: depth-reject (relative),
                                        // z: hasHistory (0/1), w: neighborhood-clamp (0/1)
            float4 _TemporalMotionParams; // x: useMotionVectors (0/1), y: adaptive response scale

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

            float RawShadow(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_SsShadowRaw, sampler_point_clamp, uv, 0).r;
            }

            float2 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float raw = RawShadow(uv);
                float deviceDepth = SAMPLE_TEXTURE2D_LOD(_CameraDepthTexture, sampler_point_clamp, uv, 0).r;

            #if UNITY_REVERSED_Z
                bool background = deviceDepth <= 0.0;
            #else
                bool background = deviceDepth >= 1.0;
            #endif

                float3 worldPos = ComputeWorldSpacePosition(uv, deviceDepth, _ScreenToWorld);
                float currEye = -mul(_CurrView, float4(worldPos, 1.0)).z;

                // No usable history (first frame / resize) or no geometry here -> pass raw through.
                if (_TemporalParams.z < 0.5 || background)
                    return float2(raw, currEye);

                // Find where THIS pixel's surface was on screen last frame.
                float2 prevUV;
                if (_TemporalMotionParams.x > 0.5)
                {
                    // Motion-vector reprojection: follows BOTH camera and per-object motion, so a
                    // shadow-receiving surface that MOVES samples the history where it actually was,
                    // instead of where a static-world assumption would put it. (Note this reprojects
                    // the RECEIVER's motion; a shadow cast by a moving caster onto static ground still
                    // leans on the neighborhood clamp — the ground itself isn't moving.) Convention:
                    // motion = currUV - prevUV  ->  prevUV = uv - motion.
                    float2 motion = SAMPLE_TEXTURE2D_LOD(_CameraMotionVectorsTexture, sampler_point_clamp, uv, 0).xy;
                    prevUV = uv - motion;
                }
                else
                {
                    // Fallback (motion vectors off): camera-only reprojection through last frame's VP.
                    float4 prevClip = mul(_PrevViewProj, float4(worldPos, 1.0));
                    float2 prevNdc = prevClip.xy / prevClip.w;
                #if UNITY_UV_STARTS_AT_TOP
                    // Mirror the Y-flip ComputeWorldSpacePosition baked in above, so reprojected UVs
                    // land on the same texels the history was written at (else it's sampled mirrored
                    // -> the disocclusion test fails every frame -> no accumulation).
                    prevNdc.y = -prevNdc.y;
                #endif
                    prevUV = prevNdc * 0.5 + 0.5;
                }

                // It was off-screen last frame -> nothing to accumulate against.
                if (any(prevUV < 0.0) || any(prevUV > 1.0))
                    return float2(raw, currEye);

                float2 hist = SAMPLE_TEXTURE2D_LOD(_SsShadowHistory, sampler_linear_clamp, prevUV, 0).rg;
                float histShadow = hist.r;
                float histEye = hist.g;

                // Disocclusion test: this world point's depth in LAST frame's view should match the
                // eye depth stored at the texel we reprojected to. A mismatch means a different
                // surface occupied that texel last frame (e.g. it was behind an object) -> reject.
                float prevEye = -mul(_PrevView, float4(worldPos, 1.0)).z;
                bool disocclude = abs(prevEye - histEye) > _TemporalParams.y * max(prevEye, 1e-3);

                // Neighborhood clamp — the main anti-ghosting tool. Pull history into the range the
                // CURRENT frame considers plausible here (min/max of the 3x3 raw shadow). A moving
                // shadow edge changes that local range, so stale history gets clamped toward the
                // new value instead of smearing a trail.
                float clampCorrection = 0.0; // how far the clamp had to move history (noise-robust)
                if (_TemporalParams.w > 0.5)
                {
                    float mn = raw, mx = raw;
                    [unroll] for (int y = -1; y <= 1; y++)
                    {
                        [unroll] for (int x = -1; x <= 1; x++)
                        {
                            float s = RawShadow(uv + float2(x, y) * _SsShadowScreenSize.xy);
                            mn = min(mn, s);
                            mx = max(mx, s);
                        }
                    }
                    float clamped = clamp(histShadow, mn, mx);
                    clampCorrection = abs(histShadow - clamped);
                    histShadow = clamped;
                }

                // Adaptive response: bias toward the current frame in proportion to how far history
                // fell OUTSIDE the current neighborhood range — i.e. how stale it is. Using the CLAMP
                // CORRECTION (not raw-vs-history) is deliberately noise-robust: the [mn,mx] range
                // brackets the per-frame PCSS dither, so a static-but-noisy penumbra reads zero
                // correction and keeps full smoothing, while a ghost trail (history left the range)
                // snaps. _TemporalMotionParams.y scales it (TemporalShadowResponse); 0 = plain alpha.
                float baseAlpha = _TemporalParams.x;
                float adaptiveAlpha = saturate(baseAlpha + clampCorrection * _TemporalMotionParams.y);

                // On disocclusion, take the current frame outright (alpha = 1) — no valid history.
                float alpha = disocclude ? 1.0 : adaptiveAlpha;
                float resolved = lerp(histShadow, raw, alpha);
                return float2(resolved, currEye);
            }
            ENDHLSL
        }
    }
}
