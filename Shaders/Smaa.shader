Shader "TDRP/Smaa"
{
    // SMAA 1x (Jimenez et al. 2012) — morphological, single-frame anti-aliasing. No temporal
    // history, so no ghosting: the reason it's chosen here over TAA for a maximum-clarity target.
    //
    // Three stages, one per pass:
    //   Pass 0 — Edge detection    : color edges -> RG mask (gamma, per-channel max delta)
    //   Pass 1 — Blend weight calc : edges + AreaTex/SearchTex -> RGBA weights (reference SMAA)
    //   Pass 2 — Neighborhood blend: color + weights -> AA'd color              (reference SMAA)
    //
    // Runs POST-tonemap on the LDR buffer (_CameraColorLdr, published by TonemapPass). Passes 1/2
    // delegate to the vendored reference (Includes/SMAA.hlsl -> SMAA_Reference.hlsl) whose SearchTex
    // addressing has exact magic constants not worth re-deriving.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Cull Off
        ZWrite Off
        ZTest Always

        // ---------------------------------------------------------------------------------------
        // Pass 0 — Edge detection.
        Pass
        {
            Name "SMAA Edge Detection"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Normals.hlsl" // _CameraNormalsTexture (full-res world normals)

            TEXTURE2D(_CameraColorLdr); // tonemapped LDR scene (global, from TonemapPass)
            SAMPLER(sampler_point_clamp);

            float4 _SmaaRTMetrics; // (1/w, 1/h, w, h)

            #define SMAA_THRESHOLD 0.1
            #define SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR 2.0

            // Normal predication. A color threshold of 0.1 is right for lit edges but too coarse for
            // a crease in shadow: both sides sit low on the tonemap curve, so a locally high-contrast
            // edge produces a small ABSOLUTE delta and slips under the bar. Where the world normal
            // turns, we KNOW there's a real edge, so lerp the per-axis threshold down toward a floor.
            // This can't come from depth — a convex crease has continuous depth — hence normals.
            // Self-limiting on smooth surfaces: adjacent normals differ by ~1deg there, so g ~ 0.
            #define SMAA_THRESHOLD_FLOOR 0.01 // predicated threshold at a hard (~90deg) crease
            #define SMAA_PRED_SHARPNESS  4.0  // how fast a normal-turn ramps the threshold to the floor

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

            // Perceptual (gamma) color. _CameraColorLdr is sRGB, so the sampler returns LINEAR; SMAA
            // is tuned for perceptual values, so re-encode with a cheap sqrt (~gamma 2.0). Edge
            // detection is a relative comparison, so the exact sRGB curve isn't required.
            float3 SmaaColor(float2 uv)
            {
                float3 c = SAMPLE_TEXTURE2D_LOD(_CameraColorLdr, sampler_point_clamp, uv, 0).rgb;
                return sqrt(saturate(c));
            }

            // Largest per-channel difference. Unlike a luma dot this catches CHROMATIC edges of near-
            // equal luma (e.g. a semi-transparent red cube over a similar-luma grey floor), at the
            // cost of sampling all 3 channels.
            float ChromaDelta(float3 a, float3 b)
            {
                float3 t = abs(a - b);
                return max(max(t.r, t.g), t.b);
            }

            // Raw normal sample (NOT normalised): the background clears to (0,0,0), so length<0.5
            // flags "no geometry here" — used to gate predication and avoid normalize(0) = NaN.
            float3 SmaaNormalRaw(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_CameraNormalsTexture, sampler_point_clamp, uv, 0).xyz;
            }

            float2 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 texel = _SmaaRTMetrics.xy;

                float3 C = SmaaColor(uv);
                float3 Cleft = SmaaColor(uv + float2(-texel.x, 0.0));
                float3 Ctop  = SmaaColor(uv + float2(0.0, -texel.y));

                float2 delta = float2(ChromaDelta(C, Cleft), ChromaDelta(C, Ctop));

                // Normal predication: lower the threshold per axis where the world normal turns.
                // Sample at the same left/top taps as the color so the axes line up.
                float3 nCr = SmaaNormalRaw(uv);
                float3 nLr = SmaaNormalRaw(uv + float2(-texel.x, 0.0));
                float3 nTr = SmaaNormalRaw(uv + float2(0.0, -texel.y));
                float lC = length(nCr), lL = length(nLr), lT = length(nTr);
                // Valid only where BOTH taps carry a real geometric normal (unit length). At a
                // silhouette against the background one side is 0 -> predication off, color decides.
                float2 gValid = float2(step(0.5, lC) * step(0.5, lL), step(0.5, lC) * step(0.5, lT));
                float3 nC = lC > 0.5 ? nCr / lC : float3(0.0, 0.0, 0.0);
                float3 nL = lL > 0.5 ? nLr / lL : float3(0.0, 0.0, 0.0);
                float3 nT = lT > 0.5 ? nTr / lT : float3(0.0, 0.0, 0.0);
                float2 g = float2(1.0 - saturate(dot(nC, nL)), 1.0 - saturate(dot(nC, nT))) * gValid;
                g = saturate(g * SMAA_PRED_SHARPNESS);
                float2 threshold = lerp(SMAA_THRESHOLD.xx, SMAA_THRESHOLD_FLOOR.xx, g);

                float2 edges = step(threshold, delta);

                if (dot(edges, 1.0) == 0.0)
                    discard;

                float3 Cright  = SmaaColor(uv + float2(texel.x, 0.0));
                float3 Cbottom = SmaaColor(uv + float2(0.0, texel.y));
                float2 deltaRB = float2(ChromaDelta(C, Cright), ChromaDelta(C, Cbottom));
                float2 maxDelta = max(delta, deltaRB);

                float3 Cleftleft = SmaaColor(uv + float2(-2.0 * texel.x, 0.0));
                float3 Ctoptop   = SmaaColor(uv + float2(0.0, -2.0 * texel.y));
                float2 deltaLL = float2(ChromaDelta(Cleft, Cleftleft), ChromaDelta(Ctop, Ctoptop));
                maxDelta = max(maxDelta, deltaLL);

                float finalDelta = max(maxDelta.x, maxDelta.y);
                edges *= step(finalDelta, SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR * delta);

                return edges;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------------------------
        // Pass 1 — Blend weight calculation.
        Pass
        {
            Name "SMAA Blend Weights"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/SMAA.hlsl" // adapter -> vendored reference (declares _SmaaRTMetrics)

            TEXTURE2D(_SmaaEdgesTex);  // pass-0 output (global)
            TEXTURE2D(_SmaaAreaTex);   // precomputed SMAA area LUT
            TEXTURE2D(_SmaaSearchTex); // precomputed SMAA search LUT

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

            float4 Frag(Varyings input) : SV_Target
            {
                // Compute the search/crossing offsets (normally a VS job) then classify the pattern
                // and look up its coverage area. subsampleIndices = 0 for plain SMAA 1x.
                float2 pixcoord;
                float4 offset[3];
                SMAABlendingWeightCalculationVS(input.uv, pixcoord, offset);

                return SMAABlendingWeightCalculationPS(input.uv, pixcoord, offset,
                    _SmaaEdgesTex, _SmaaAreaTex, _SmaaSearchTex, float4(0.0, 0.0, 0.0, 0.0));
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------------------------
        // Pass 2 — Neighborhood blending.
        Pass
        {
            Name "SMAA Neighborhood Blend"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/SMAA.hlsl"

            TEXTURE2D(_CameraColorLdr); // tonemapped LDR scene (global)
            TEXTURE2D(_SmaaBlendTex);   // pass-1 weights (global)

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

            float4 Frag(Varyings input) : SV_Target
            {
                float4 offset;
                SMAANeighborhoodBlendingVS(input.uv, offset);
                // Blends in the LDR sampler's space (linear, since _CameraColorLdr is sRGB); the
                // weighted average of neighbours is well-behaved either way. Output written to the
                // sRGB present buffer.
                return SMAANeighborhoodBlendingPS(input.uv, offset, _CameraColorLdr, _SmaaBlendTex);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
