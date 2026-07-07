Shader "TDRP/WeightedOitComposite"
{
    // Resolve pass for Weighted Blended OIT. Reads the two accumulation buffers filled by the
    // transparent materials' TDRPTransparentOit pass and composites the result over the existing
    // (opaque + skybox) CameraColor in one fullscreen draw.
    SubShader
    {
        Tags { "RenderType" = "Transparent" }

        Pass
        {
            Name "WeightedOitComposite"

            // out = src.rgb*(1 - src.a) + dst.rgb*src.a, with src.a = revealage. This is exactly
            // the WBOIT composite: averagedColor*(1 - revealage) + background*revealage.
            Blend OneMinusSrcAlpha SrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURE2D(_OitAccum);
            TEXTURE2D(_OitRevealage);
            SAMPLER(sampler_point_clamp);

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
                float4 accum = SAMPLE_TEXTURE2D_LOD(_OitAccum, sampler_point_clamp, input.uv, 0);
                float revealage = SAMPLE_TEXTURE2D_LOD(_OitRevealage, sampler_point_clamp, input.uv, 0).r;

                // Weighted-average color: undo the summed weight. Guard the divide for pixels
                // with no transparent coverage (accum.a == 0). Those also have revealage == 1,
                // so the blend keeps the background regardless of this rgb.
                float3 averageColor = accum.rgb / max(accum.a, 1e-5);

                // src.rgb = average color, src.a = revealage (the background-preserve factor).
                return float4(averageColor, revealage);
            }
            ENDHLSL
        }
    }
}
