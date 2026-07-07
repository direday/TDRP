Shader "TDRP/DefaultUnlit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "TopDownRenderPipeline"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Includes/TDRPCore.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "TDRPUnlit" }

            Cull Back
            ZWrite Off
            ZTest Equal

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                return _BaseColor;
            }
            ENDHLSL
        }

        Pass
        {
            // Even an unlit opaque surface occludes geometry, so it belongs in the depth +
            // normal prepass (it contributes depth for early-Z and a normal for SSAO/shadows).
            Name "DepthNormals"
            Tags { "LightMode" = "TDRPDepthOnly" }

            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/Normals.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                return float4(EncodeSceneNormalWS(normalize(input.normalWS)), 0.0);
            }
            ENDHLSL
        }
    }
}
