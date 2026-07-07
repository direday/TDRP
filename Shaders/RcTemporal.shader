Shader "TDRP/RcTemporal"
{
    // Temporal resolve for the screen-space Radiance Cascades GI. Blends this frame's diffuse
    // irradiance (_RcIrradiance, RGB) with a reprojected history buffer, so the per-frame quantization
    // crawl the fixed screen-space probe grid produces (a world point crossing probe cells as the
    // camera/objects move samples different discretized radiance each frame) is averaged down instead
    // of flickering. Same kernel as the SSAO/shadow temporal resolves, widened from a scalar to RGB:
    // history holds RGB irradiance + eye depth (A). Reprojection follows the motion vector buffer when
    // available (_TemporalMotionParams.x) so movers sample the right history; otherwise camera-only.
    // The per-channel neighborhood clamp guards both paths against ghosting.
    //
    // NOTE: this smooths the frame-to-frame DELTA (the visible shimmer). It does not by itself improve
    // the converged spatial accuracy — the grid is still sampled at the same places every frame. That
    // needs per-frame probe/ray jitter (a later step) so the temporal average reconstructs a denser
    // effective grid rather than converging to the same quantized value.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "RcTemporal"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURE2D(_CameraDepthTexture); // current device depth (global, from the prepass)
            TEXTURE2D(_RcIrradiance);       // this frame's RC irradiance (global, RGB), pre-temporal
            TEXTURE2D(_RcHistory);          // last frame's resolved (RGB irradiance, A = eye depth); on the material
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
            float4 _TemporalMotionParams; // x: useMotionVectors (0/1), y: response scale

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

            float3 CurrentRc(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_RcIrradiance, sampler_point_clamp, uv, 0).rgb;
            }

            float Luminance709(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float3 rc = CurrentRc(uv);
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
                    return float4(rc, currEye);

                // Find where THIS pixel's surface was on screen last frame.
                float2 prevUV;
                if (_TemporalMotionParams.x > 0.5)
                {
                    // Motion-vector reprojection: follows BOTH camera and per-object motion, so a moving
                    // object samples the history at its actual previous location instead of where a
                    // static-world assumption would put it. Convention: motion = currUV - prevUV. The
                    // buffer is full-res; sampling at this (possibly half-res) uv resolves correctly.
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
                    return float4(rc, currEye);

                float4 hist = SAMPLE_TEXTURE2D_LOD(_RcHistory, sampler_linear_clamp, prevUV, 0);
                float3 histRc = hist.rgb;
                float histEye = hist.a;

                // Disocclusion test: this world point's depth in LAST frame's view should match the eye
                // depth stored at the texel we reprojected to. A mismatch means a different surface
                // occupied that texel last frame -> reject the history. SOFT (ramped, not binary): a hard
                // threshold makes silhouette-edge pixels flip between "keep history" and "take raw current"
                // as sub-pixel reprojection wobble nudges the depth delta across the line each frame — that
                // per-frame flip IS the edge shimmer seen at rest. Ramping over a tolerance band gives a
                // marginal edge a stable in-between blend; only a real disocclusion (delta past 2x tol)
                // fully drops history.
                float prevEye = -mul(_PrevView, float4(worldPos, 1.0)).z;
                float depthTol = _TemporalParams.y * max(prevEye, 1e-3);
                float reject = smoothstep(depthTol, depthTol * 2.0, abs(prevEye - histEye));

                // Neighborhood clamp — pull history into the per-channel min/max of the CURRENT 3x3 RC,
                // so a moving lighting edge clamps stale history toward the new value instead of smearing
                // a bright/dark trail. Per-channel keeps hue changes (a colored bounce moving off a
                // surface) from bleeding.
                if (_TemporalParams.w > 0.5)
                {
                    float3 mn = rc, mx = rc;
                    [unroll] for (int y = -1; y <= 1; y++)
                    {
                        [unroll] for (int x = -1; x <= 1; x++)
                        {
                            float3 s = CurrentRc(uv + float2(x, y) * _TemporalScreenSize.xy);
                            mn = min(mn, s);
                            mx = max(mx, s);
                        }
                    }
                    histRc = clamp(histRc, mn, mx);
                }

                // Adaptive response: where the current RC STILL disagrees with the (clamped) history —
                // a moving occluder/emitter the receiver's motion vector can't capture — bias the blend
                // toward the current frame in proportion to the (luminance) disagreement, so it snaps
                // instead of trailing. _TemporalMotionParams.y scales how hard that pushes
                // (RcTemporalResponse); 0 = plain RcTemporalAlpha everywhere (max smoothing).
                float baseAlpha = _TemporalParams.x;
                float change = abs(Luminance709(rc) - Luminance709(histRc));
                float adaptiveAlpha = saturate(baseAlpha + change * _TemporalMotionParams.y);

                // Blend toward current in proportion to the (soft) rejection: the adaptive/accumulating
                // alpha where history is trustworthy, ramping to 1.0 only for a genuine disocclusion.
                float alpha = lerp(adaptiveAlpha, 1.0, reject);
                float3 resolved = lerp(histRc, rc, alpha);
                return float4(resolved, currEye);
            }
            ENDHLSL
        }
    }
}
