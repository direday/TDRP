Shader "TDRP/BrdfLutDebug"
{
    // Debug view of the environment-BRDF integration LUT. X = NdotV (grazing on the left), Y =
    // perceptual roughness (smooth at the bottom). R = the F0 scale term, G = the bias term; shown
    // as red+green so the canonical split-sum LUT shape is recognizable (bright where the Fresnel
    // bias lifts grazing angles).
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "BrdfLutDebug"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURE2D(_BrdfLutTex);
            SAMPLER(sampler_linear_clamp);

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
                float2 dfg = SAMPLE_TEXTURE2D_LOD(_BrdfLutTex, sampler_linear_clamp, input.uv, 0).rg;
                return float4(dfg.x, dfg.y, 0.0, 1.0);
            }
            ENDHLSL
        }
    }
}
