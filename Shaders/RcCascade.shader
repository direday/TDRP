Shader "TDRP/RcCascade"
{
    // Builds ONE cascade of the Radiance Cascades hierarchy (and merges the cascade above it into this
    // one). Run top-down: the highest cascade first (no upper to merge), then each lower cascade reads
    // the one above. By the time cascade 0 is built it already contains the fully-merged radiance for
    // every direction from 0 out to the hierarchy's max reach, so the resolve only needs cascade 0.
    //
    // STORAGE (probe atlas): this cascade's texture is a grid of probes; each probe owns a d x d block
    // of texels, one per ray direction (D = d*d directions, azimuths around the screen plane). Probe
    // spacing s and block side d both double every cascade, so the cascade textures stay ~constant size
    // — the defining RC property (spatial resolution falls as angular resolution rises).
    //
    // PER TEXEL: decode (probe, direction); place the probe in the world from the depth buffer; march
    // this cascade's interval [t0, t1] along that direction. On a hit -> the previous frame's colour
    // there is the radiance from that direction (sky-floored). On a miss -> continue the ray into the
    // cascade above: average the 4 CHILD directions (angular res is 4x up there) of the nearest upper
    // probe. At the top cascade a miss just sees the sky. This "continue on miss" IS the merge.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "RcCascade"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURE2D(_CameraDepthTexture);
            SAMPLER(sampler_point_clamp);
            TEXTURE2D(_RcPrevColor);
            SAMPLER(sampler_linear_clamp);
            TEXTURE2D(_RcUpperCascade); // cascade i+1 (only bound/used when merging)

            float4x4 _ScreenToWorld;
            float4x4 _WorldToScreen;
            float4x4 _RcPrevWorldToScreen; // previous frame's view-proj (reprojects the bounce-colour fetch)
            float4x4 _RcView;

            float4 _AmbientSkyColor;
            float4 _AmbientGroundColor;

            float4 _RcScreenSize; // xy: W,H  zw: 1/W,1/H
            float4 _RcCascade;    // x: d (block side), y: s (probe spacing px), z: D (dirs), w: index
            float4 _RcInterval;   // x: t0, y: t1 (world), z: steps, w: hasUpper (1/0)
            float4 _RcUpper;      // x: d_up, y: s_up (px), z: upperTexW, w: upperTexH
            float4 _RcMisc;       // x: depth bias (world), y: sky floor, z: falloff distance (0=off), w: firefly clamp
            float4 _RcJitter;     // xy: per-frame sub-cell probe offset in [-0.5,0.5] cells (0 when temporal off)

            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                return o;
            }

            float SampleDepth(float2 uv) { return SAMPLE_TEXTURE2D_LOD(_CameraDepthTexture, sampler_point_clamp, uv, 0).r; }

            bool IsBackground(float d)
            {
            #if UNITY_REVERSED_Z
                return d <= 0.0;
            #else
                return d >= 1.0;
            #endif
            }

            float EyeDepth(float3 p) { return -mul(_RcView, float4(p, 1.0)).z; }

            #define RC_DEPTH_SIGMA_REL 0.05 // upper-probe depth tolerance as a fraction of eye depth

            float3 SampleAmbient(float3 dir) { return lerp(_AmbientGroundColor.rgb, _AmbientSkyColor.rgb, dir.y * 0.5 + 0.5); }

            float2 WorldToScreenUV(float3 p, out bool onScreen)
            {
                float4 clip = mul(_WorldToScreen, float4(p, 1.0));
                float2 ndc = clip.xy / clip.w;
            #if UNITY_UV_STARTS_AT_TOP
                ndc.y = -ndc.y;
            #endif
                float2 uv = ndc * 0.5 + 0.5;
                onScreen = !(any(uv < 0.0) || any(uv > 1.0));
                return uv;
            }

            // Reproject a world point into the PREVIOUS frame's screen UV. The bounce colour lives in
            // last frame's image (_RcPrevColor), so sampling it at THIS frame's uv lags when the camera
            // moves — the point sat elsewhere on screen last frame. Projecting by the previous view-proj
            // cancels that for static geometry. `valid` is false if the point was behind the previous
            // camera (w<=0) or off last frame's screen (disocclusion) — no history there.
            float2 WorldToPrevScreenUV(float3 p, out bool valid)
            {
                float4 clip = mul(_RcPrevWorldToScreen, float4(p, 1.0));
                float2 ndc = clip.xy / clip.w;
            #if UNITY_UV_STARTS_AT_TOP
                ndc.y = -ndc.y;
            #endif
                float2 uv = ndc * 0.5 + 0.5;
                valid = clip.w > 0.0 && !(any(uv < 0.0) || any(uv > 1.0));
                return uv;
            }

            // Continue ray `k` into the cascade above: average its 4 CHILD directions (angular res is
            // 4x up there), each interpolated across the 4 nearest upper probes. The interpolation is
            // both BILINEAR (RC's "ringing fix" — nearest-probe merge leaves blocky bands) and
            // DEPTH-AWARE: each upper probe is also weighted by how close its surface depth is to this
            // probe's, so the merge doesn't drag radiance across a silhouette (an upper probe sitting on
            // a foreground object, whose radiance is for a different point in space, gets rejected).
            float3 MergeUpper(float2 probePixel, int k, float centerEye)
            {
                int dUp = (int)_RcUpper.x;
                float sUp = _RcUpper.y;
                int upGridW = (int)_RcUpper.z / dUp;
                int upGridH = (int)_RcUpper.w / dUp;

                // Fractional upper-probe coordinate (upper probe centres at (idx+0.5+jitter)*sUp, so
                // invert with the same jitter) + bilinear weights.
                float2 pc = probePixel / sUp - 0.5 - _RcJitter.xy;
                int2 p0 = (int2)floor(pc);
                float2 f = pc - p0;
                float4 bw = float4((1 - f.x) * (1 - f.y), f.x * (1 - f.y), (1 - f.x) * f.y, f.x * f.y);
                int2 pOff[4] = { int2(0,0), int2(1,0), int2(0,1), int2(1,1) };

                // Per-probe weight = bilinear x depth-similarity (computed once, reused for all 4 children).
                int2 probe[4];
                float w[4];
                float wsum = 0.0;
                [unroll]
                for (int j = 0; j < 4; j++)
                {
                    int2 p = clamp(p0 + pOff[j], int2(0, 0), int2(upGridW - 1, upGridH - 1));
                    probe[j] = p;
                    float2 upUV = ((float2)p + 0.5 + _RcJitter.xy) * sUp * _RcScreenSize.zw;
                    float upd = SampleDepth(upUV);
                    float wd = 0.0;
                    if (!IsBackground(upd))
                    {
                        float upEye = EyeDepth(ComputeWorldSpacePosition(upUV, upd, _ScreenToWorld));
                        float dz = (upEye - centerEye) / (centerEye * RC_DEPTH_SIGMA_REL + 1e-4);
                        wd = exp(-dz * dz);
                    }
                    w[j] = bw[j] * wd;
                    wsum += w[j];
                }
                // If every upper probe was rejected (all background / far surface), fall back to plain
                // bilinear rather than returning black.
                if (wsum < 1e-5)
                {
                    w[0] = bw[0]; w[1] = bw[1]; w[2] = bw[2]; w[3] = bw[3];
                    wsum = 1.0;
                }

                float3 sum = 0.0;
                [unroll]
                for (int c = 0; c < 4; c++)
                {
                    int kc = 4 * k + c;            // the 4 finer directions covering k's arc
                    int cdx = kc % dUp;
                    int cdy = kc / dUp;
                    [unroll]
                    for (int j = 0; j < 4; j++)
                    {
                        int2 texel = int2(probe[j].x * dUp + cdx, probe[j].y * dUp + cdy);
                        sum += LOAD_TEXTURE2D(_RcUpperCascade, texel).rgb * w[j];
                    }
                }
                return sum / wsum * 0.25;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                int2 texel = int2(input.positionCS.xy);
                int d = (int)_RcCascade.x;
                float s = _RcCascade.y;
                int D = (int)_RcCascade.z;

                int px = texel.x / d;
                int py = texel.y / d;
                int dx = texel.x - px * d;
                int dy = texel.y - py * d;
                int k = dy * d + dx;

                float theta = (k + 0.5) / (float)D * TWO_PI;

                // World-space camera basis spanning the screen plane (rows of world->view).
                float3 camRight = float3(_RcView._m00, _RcView._m01, _RcView._m02);
                float3 camUp    = float3(_RcView._m10, _RcView._m11, _RcView._m12);
                float3 dirWS = camRight * cos(theta) + camUp * sin(theta);
                float3 skyRad = SampleAmbient(dirWS);

                // Probe centre in the screen. The per-frame sub-cell jitter shifts every probe by the
                // same fraction of its cell each frame; with temporal accumulation reprojecting + blending
                // the result, the moving sample positions reconstruct a denser effective probe grid, which
                // dissolves the fixed-grid crawl that a static grid leaves at large spacing. The merge and
                // the resolve apply the SAME offset so the hierarchy stays aligned within a frame.
                float2 probePixel = (float2(px, py) + 0.5 + _RcJitter.xy) * s;
                float2 probeUV = probePixel * _RcScreenSize.zw;
                float pd = SampleDepth(probeUV);
                if (IsBackground(pd))
                    return float4(skyRad, 1.0); // probe over background -> just sky in every direction

                float3 P = ComputeWorldSpacePosition(probeUV, pd, _ScreenToWorld);
                float centerEye = EyeDepth(P);

                float t0 = _RcInterval.x;
                float t1 = _RcInterval.y;
                int steps = (int)_RcInterval.z;
                float bias = _RcMisc.x;
                float skyFloor = _RcMisc.y;
                float falloffDist = _RcMisc.z;

                // March this cascade's interval [t0, t1] along the direction.
                float3 radiance = skyRad;   // default = miss-at-top (overwritten below on hit/merge)
                bool hit = false;
                for (int i = 1; i <= steps; i++)
                {
                    float t = lerp(t0, t1, (float)i / (float)steps);
                    float3 marchWS = P + dirWS * t;

                    bool onScreen;
                    float2 suv = WorldToScreenUV(marchWS, onScreen);
                    if (!onScreen)
                        break; // left the screen -> treat as a miss (continue upper / sky)

                    float sd = SampleDepth(suv);
                    if (IsBackground(sd))
                        continue;

                    float3 sceneP = ComputeWorldSpacePosition(suv, sd, _ScreenToWorld);
                    float sceneEye = EyeDepth(sceneP);
                    float marchEye = EyeDepth(marchWS);
                    if (sceneEye < marchEye - bias)
                    {
                        // Fetch the bounce colour from where this hit surface sat LAST frame, not where
                        // it sits now — cancels camera-motion lag. Disoccluded/off-screen last frame ->
                        // fall back to the current uv (minor error at frame edges, no valid history).
                        bool prevValid;
                        float2 prevUV = WorldToPrevScreenUV(sceneP, prevValid);
                        float2 fetchUV = prevValid ? prevUV : suv;
                        float3 hitColor = SAMPLE_TEXTURE2D_LOD(_RcPrevColor, sampler_linear_clamp, fetchUV, 0).rgb;
                        // Firefly clamp: cap the bounce radiance's luminance (hue-preserving) so one very
                        // bright prev-frame texel — emissive, specular highlight — can't spike this ray. Those
                        // spikes are what the coarse fan samples inconsistently frame-to-frame -> the flickering
                        // "specks" on movement. _RcMisc.w = max luminance (0 = disabled).
                        float maxLum = _RcMisc.w;
                        if (maxLum > 0.0)
                        {
                            float lum = dot(hitColor, float3(0.2126, 0.7152, 0.0722));
                            if (lum > maxLum)
                                hitColor *= maxLum / lum;
                        }
                        // Distance falloff: coarse angular sampling doesn't shrink a far object's solid
                        // angle, so its bounce would halo at full strength. Attenuate it inverse-square-ish
                        // with the hit distance (0 = disabled). Folded into the sky floor: a fully
                        // attenuated hit reads as pure sky (no bounce).
                        float atten = falloffDist > 0.0 ? 1.0 / (1.0 + (t / falloffDist) * (t / falloffDist)) : 1.0;
                        float skyF = 1.0 - (1.0 - skyFloor) * atten;
                        radiance = lerp(hitColor, skyRad, skyF);
                        hit = true;
                        break;
                    }
                }

                // On a miss, continue the ray: into the upper cascade if merging, else the sky.
                if (!hit)
                {
                    if (_RcInterval.w > 0.5)
                        radiance = MergeUpper(probePixel, k, centerEye);
                    else
                        radiance = skyRad;
                }

                return float4(radiance, 1.0);
            }
            ENDHLSL
        }
    }
}
