Shader "TDRP/Fsr"
{
    // AMD FidelityFX Super Resolution 1.0 — a SPATIAL (single-frame) upscaler. Two passes:
    //   Pass 0  EASU (Edge-Adaptive Spatial Upsampling) : render-res -> display-res, edge-directed.
    //   Pass 1  RCAS (Robust Contrast-Adaptive Sharpen) : sharpen at display-res (undo EASU softness).
    //
    // Unlike FSR2/DLSS this needs NO motion vectors, jitter, or history — it reconstructs from one
    // frame — which is why it drops straight into our post-tonemap present path. AMD's guidance: feed
    // it a clean, already-anti-aliased image (our SMAA output), because a spatial upscaler magnifies
    // whatever aliasing is already there rather than resolving it temporally.
    //
    // Both passes are driven by a legacy cmd.Blit (see FsrUpscalePass), so the vertex mirrors
    // Tonemap.shader (a fullscreen quad with POSITION+TEXCOORD0) and the source arrives as _MainTex.
    //
    // COLOR SPACE: our LDR buffers are R8G8B8A8_SRGB, so the hardware decodes sRGB->linear on read and
    // re-encodes linear->sRGB on write (the same round-trip SmaaBlendPass relies on). FSR therefore
    // runs in LINEAR light here. AMD nominally recommends a perceptual (gamma) working space; the
    // difference is a slight ringing/contrast nuance on bright edges, negligible for this content, and
    // running in linear lets us reuse the existing sRGB present path unchanged.
    //
    // The EASU direction/length estimation and its sin/cos-free Lanczos-2 approximation are ported
    // verbatim (constants and all) from AMD's ffx_fsr1.h — the same "magic constants not worth
    // re-deriving" reason we vendor the SMAA reference. Reciprocals that AMD does with a finite
    // low-precision approximation are written here as guarded rcp() so flat black/white regions can't
    // produce Inf/NaN (an exact rcp(0) would; AMD's approximate rcp stays finite).

    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #include "Includes/TDRPCore.hlsl"

        TEXTURE2D(_MainTex);          // source, bound as a global before the draw
        SAMPLER(sampler_LinearClamp); // inline sampler (name-recognised): clamp addressing for gather

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
        };

        // Fullscreen triangle via SV_VertexID (DrawProcedural, 3 verts) — the same deterministic
        // coverage the SMAA passes use. (An earlier cmd.Blit-based version relied on the legacy blit
        // quad; chained inside RenderGraph unsafe passes it failed to cover the destination -> black.)
        Varyings Vert(uint vertexID : SV_VertexID)
        {
            Varyings output;
            output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
            return output;
        }
        ENDHLSL

        // -------------------------------------------------------------------------------------------
        // Pass 0 — EASU: edge-adaptive upscale to display resolution.
        Pass
        {
            Name "FSR EASU"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            // FsrEasuCon outputs (computed CPU-side in FsrUpscalePass). con0 maps an output pixel to
            // input-pixel space (scale + half-texel bias); con1..con3 are the four gather-centre
            // offsets, in input UV, that tile the 12-tap footprint.
            float4 _EasuCon0;
            float4 _EasuCon1;
            float4 _EasuCon2;
            float4 _EasuCon3;

            // Per-quad accumulation of the dominant edge direction (dir) and its strength (len) from
            // one of the four bilinear-weighted sub-positions. Straight port of AMD's FsrEasuSetF.
            void EasuSet(inout float2 dir, inout float len, float2 pp,
                bool biS, bool biT, bool biU, bool biV,
                float lA, float lB, float lC, float lD, float lE)
            {
                // Bilinear weight of this sub-position within the fractional cell.
                float w = 0.0;
                if (biS) w = (1.0 - pp.x) * (1.0 - pp.y);
                if (biT) w =        pp.x  * (1.0 - pp.y);
                if (biU) w = (1.0 - pp.x) *        pp.y;
                if (biV) w =        pp.x  *        pp.y;

                // Horizontal contribution. dirX = luma slope across the row; lenX = how "edge-like"
                // (slope normalised by the local luma range, squared). Guarded divide == AMD's
                // ASatF1(|dirX| * APrxLoRcp(max(|dc|,|cb|))) but Inf-safe when the row is flat.
                float dc = lD - lC;
                float cb = lC - lB;
                float dirX = lD - lB;
                dir.x += dirX * w;
                float lenX = saturate(abs(dirX) * rcp(max(max(abs(dc), abs(cb)), 1e-6)));
                lenX *= lenX;
                len += lenX * w;

                // Vertical contribution (same, down the column).
                float ec = lE - lC;
                float ca = lC - lA;
                float dirY = lE - lA;
                dir.y += dirY * w;
                float lenY = saturate(abs(dirY) * rcp(max(max(abs(ec), abs(ca)), 1e-6)));
                lenY *= lenY;
                len += lenY * w;
            }

            // One filter tap: rotate the tap offset into edge-aligned space, stretch by anisotropy,
            // weight by the Lanczos-2 approximation. Port of AMD's FsrEasuTapF.
            void EasuTap(inout float3 aC, inout float aW, float2 off, float2 dir, float2 len,
                float lob, float clp, float3 c)
            {
                float2 v;
                v.x = (off.x * ( dir.x)) + (off.y * dir.y);
                v.y = (off.x * (-dir.y)) + (off.y * dir.x);
                v *= len;
                float d2 = v.x * v.x + v.y * v.y;
                d2 = min(d2, clp);
                // sin/cos-free Lanczos(2): wB is the main lobe, wA the window; both squared.
                float wB = (2.0 / 5.0) * d2 + (-1.0);
                float wA = lob * d2 + (-1.0);
                wB *= wB;
                wA *= wA;
                wB = (25.0 / 16.0) * wB + (-(25.0 / 16.0 - 1.0));
                float w = wB * wA;
                aC += c * w;
                aW += w;
            }

            // Min3/Max3 (componentwise) come from the core ShaderLibrary (Common.hlsl).

            float4 Frag(Varyings input) : SV_Target
            {
                // Output pixel index (SV_Position is pixel-centre, so truncation gives the integer).
                uint2 ip = uint2(input.positionCS.xy);

                // Resolve position in input-pixel space; fp = integer input pixel, pp = fractional.
                float2 pp = float2(ip) * _EasuCon0.xy + _EasuCon0.zw;
                float2 fp = floor(pp);
                pp -= fp;

                // Four gather centres covering the 4x4 (minus corners) = 12-tap neighbourhood.
                float2 p0 = fp * _EasuCon1.xy + _EasuCon1.zw;
                float2 p1 = p0 + _EasuCon2.xy;
                float2 p2 = p0 + _EasuCon2.zw;
                float2 p3 = p0 + _EasuCon3.xy;

                // GatherRed/Green/Blue return the 2x2 quad's channel in D3D order; the reference's
                // b/c/i/j/f/e/k/l/h/g/n/o naming maps onto these lanes.
                float4 bczzR = _MainTex.GatherRed  (sampler_LinearClamp, p0);
                float4 bczzG = _MainTex.GatherGreen(sampler_LinearClamp, p0);
                float4 bczzB = _MainTex.GatherBlue (sampler_LinearClamp, p0);
                float4 ijfeR = _MainTex.GatherRed  (sampler_LinearClamp, p1);
                float4 ijfeG = _MainTex.GatherGreen(sampler_LinearClamp, p1);
                float4 ijfeB = _MainTex.GatherBlue (sampler_LinearClamp, p1);
                float4 klhgR = _MainTex.GatherRed  (sampler_LinearClamp, p2);
                float4 klhgG = _MainTex.GatherGreen(sampler_LinearClamp, p2);
                float4 klhgB = _MainTex.GatherBlue (sampler_LinearClamp, p2);
                float4 zzonR = _MainTex.GatherRed  (sampler_LinearClamp, p3);
                float4 zzonG = _MainTex.GatherGreen(sampler_LinearClamp, p3);
                float4 zzonB = _MainTex.GatherBlue (sampler_LinearClamp, p3);

                // Approximate luma (AMD's 0.5*B + 0.5*R + G) used only for edge detection.
                float4 bczzL = bczzB * 0.5 + (bczzR * 0.5 + bczzG);
                float4 ijfeL = ijfeB * 0.5 + (ijfeR * 0.5 + ijfeG);
                float4 klhgL = klhgB * 0.5 + (klhgR * 0.5 + klhgG);
                float4 zzonL = zzonB * 0.5 + (zzonR * 0.5 + zzonG);

                float bL = bczzL.x, cL = bczzL.y;
                float iL = ijfeL.x, jL = ijfeL.y, fL = ijfeL.z, eL = ijfeL.w;
                float kL = klhgL.x, lL = klhgL.y, hL = klhgL.z, gL = klhgL.w;
                float oL = zzonL.z, nL = zzonL.w;

                // Accumulate the edge direction/length over the four sub-positions.
                float2 dir = 0.0;
                float len = 0.0;
                EasuSet(dir, len, pp, true,  false, false, false, bL, eL, fL, gL, jL);
                EasuSet(dir, len, pp, false, true,  false, false, cL, fL, gL, hL, kL);
                EasuSet(dir, len, pp, false, false, true,  false, fL, iL, jL, kL, nL);
                EasuSet(dir, len, pp, false, false, false, true,  gL, jL, kL, lL, oL);

                // Normalise direction; degenerate (flat) -> axis-aligned, len 0 -> isotropic kernel.
                float2 dir2 = dir * dir;
                float dirR = dir2.x + dir2.y;
                bool zro = dirR < (1.0 / 32768.0);
                dirR = rsqrt(dirR);
                dirR = zro ? 1.0 : dirR;
                dir.x = zro ? 1.0 : dir.x;
                dir *= dirR;

                len = len * 0.5;
                len *= len;

                // Anisotropy: stretch the kernel along the edge, squash across it.
                float stretch = (dir.x * dir.x + dir.y * dir.y) * rcp(max(abs(dir.x), abs(dir.y)));
                float2 len2 = float2(1.0 + (stretch - 1.0) * len, 1.0 + (-0.5) * len);
                // Window shifts with edge amount; clp is the tap distance clamp.
                float lob = 0.5 + ((1.0 / 4.0 - 0.04) - 0.5) * len;
                float clp = rcp(lob);

                // Ringing clamp bounds: min/max colour over the centre 2x2 (f,g,j,k).
                float3 f3 = float3(ijfeR.z, ijfeG.z, ijfeB.z);
                float3 g3 = float3(klhgR.w, klhgG.w, klhgB.w);
                float3 j3 = float3(ijfeR.y, ijfeG.y, ijfeB.y);
                float3 k3 = float3(klhgR.x, klhgG.x, klhgB.x);
                float3 minC = min(Min3(f3, g3, j3), k3);
                float3 maxC = max(Max3(f3, g3, j3), k3);

                // Weighted 12-tap gather.
                float3 aC = 0.0;
                float aW = 0.0;
                EasuTap(aC, aW, float2( 0.0, -1.0) - pp, dir, len2, lob, clp, float3(bczzR.x, bczzG.x, bczzB.x));
                EasuTap(aC, aW, float2( 1.0, -1.0) - pp, dir, len2, lob, clp, float3(bczzR.y, bczzG.y, bczzB.y));
                EasuTap(aC, aW, float2(-1.0,  1.0) - pp, dir, len2, lob, clp, float3(ijfeR.x, ijfeG.x, ijfeB.x));
                EasuTap(aC, aW, float2( 0.0,  1.0) - pp, dir, len2, lob, clp, float3(ijfeR.y, ijfeG.y, ijfeB.y));
                EasuTap(aC, aW, float2( 0.0,  0.0) - pp, dir, len2, lob, clp, float3(ijfeR.z, ijfeG.z, ijfeB.z));
                EasuTap(aC, aW, float2(-1.0,  0.0) - pp, dir, len2, lob, clp, float3(ijfeR.w, ijfeG.w, ijfeB.w));
                EasuTap(aC, aW, float2( 1.0,  1.0) - pp, dir, len2, lob, clp, float3(klhgR.x, klhgG.x, klhgB.x));
                EasuTap(aC, aW, float2( 2.0,  1.0) - pp, dir, len2, lob, clp, float3(klhgR.y, klhgG.y, klhgB.y));
                EasuTap(aC, aW, float2( 2.0,  0.0) - pp, dir, len2, lob, clp, float3(klhgR.z, klhgG.z, klhgB.z));
                EasuTap(aC, aW, float2( 1.0,  0.0) - pp, dir, len2, lob, clp, float3(klhgR.w, klhgG.w, klhgB.w));
                EasuTap(aC, aW, float2( 1.0,  2.0) - pp, dir, len2, lob, clp, float3(zzonR.z, zzonG.z, zzonB.z));
                EasuTap(aC, aW, float2( 0.0,  2.0) - pp, dir, len2, lob, clp, float3(zzonR.w, zzonG.w, zzonB.w));

                // Normalise and clamp to the local range (kills overshoot ringing).
                float3 col = min(maxC, max(minC, aC * rcp(aW)));
                return float4(col, 1.0);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------------------------------
        // Pass 1 — RCAS: robust contrast-adaptive sharpening at display resolution.
        Pass
        {
            Name "FSR RCAS"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            // x = sharpness as exp2(-stops) (0..1, higher = sharper); yz = display size for load clamp.
            float4 _RcasConfig;

            #define FSR_RCAS_LIMIT (0.25 - (1.0 / 16.0)) // 0.1875

            // Min3/Max3 (scalar) come from the core ShaderLibrary (Common.hlsl).

            float3 RcasLoad(int2 p, int2 maxCoord)
            {
                return LOAD_TEXTURE2D(_MainTex, clamp(p, int2(0, 0), maxCoord)).rgb;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                int2 sp = int2(input.positionCS.xy);
                int2 mx = int2(_RcasConfig.yz) - 1;

                // Plus-shaped neighbourhood (b top, d left, e centre, f right, h bottom).
                float3 b = RcasLoad(sp + int2( 0, -1), mx);
                float3 d = RcasLoad(sp + int2(-1,  0), mx);
                float3 e = RcasLoad(sp,               mx);
                float3 f = RcasLoad(sp + int2( 1,  0), mx);
                float3 h = RcasLoad(sp + int2( 0,  1), mx);

                // Per-channel min/max over the 4 ring taps.
                float3 mn4 = min(Min3(b, d, f), h);
                float3 mx4 = max(Max3(b, d, f), h);

                // Sharpening lobe: how far the centre can be pushed toward the local extremes without
                // overshooting past the ring's range. Guarded rcp() keeps flat black/white finite.
                float2 peakC = float2(1.0, -4.0);
                float3 hitMin = min(mn4, e) * rcp(max(4.0 * mx4, 1e-6));
                float3 hitMax = (peakC.x - max(mx4, e)) * rcp(min(4.0 * mn4 + peakC.y, -1e-6));
                float3 lobeRGB = max(-hitMin, hitMax);
                float lobe = max(-FSR_RCAS_LIMIT, min(Max3(lobeRGB.r, lobeRGB.g, lobeRGB.b), 0.0)) * _RcasConfig.x;

                float rcpL = rcp(4.0 * lobe + 1.0);
                float3 col = (lobe * b + lobe * d + lobe * h + lobe * f + e) * rcpL;
                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
