Shader "TDRP/RcDebug"
{
    // Debug view of the raw Radiance Cascades irradiance buffer (_RcIrradiance, HDR). Shown before
    // tonemap, so the pipeline routes it through the tonemap pass like the scene color — this lets you
    // see the gathered indirect field directly (colored halos around lit/emissive geometry, flat
    // ambient in the open) to A/B the gather in isolation from the surface albedo it modulates.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "RcDebug"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURE2D(_RcIrradiance);
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
                float3 rc = SAMPLE_TEXTURE2D_LOD(_RcIrradiance, sampler_point_clamp, input.uv, 0).rgb;
                return float4(rc, 1.0);
            }
            ENDHLSL
        }
    }
}
