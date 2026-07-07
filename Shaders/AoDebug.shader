Shader "TDRP/AoDebug"
{
    // Debug view of the raw screen-space AO buffer: splats the single occlusion channel to
    // grayscale (white = open, black = occluded) so it can be inspected and tuned in isolation,
    // before it's denoised or wired into shading.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "AoDebug"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURE2D(_ScreenSpaceAO);
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
                float ao = SAMPLE_TEXTURE2D_LOD(_ScreenSpaceAO, sampler_point_clamp, input.uv, 0).r;
                return float4(ao, ao, ao, 1.0);
            }
            ENDHLSL
        }
    }
}
