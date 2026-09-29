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

        // Per-instance overrides for BatchRendererGroup draws (e.g. team colour). A property
        // the BRG does not supply falls back to the material value.
        #ifdef UNITY_DOTS_INSTANCING_ENABLED
        UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
            UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
        UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

        #define _BaseColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor)
        #endif
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
            // BatchRendererGroup draws (DOTS instancing). See TDRPCore.hlsl.
            #pragma multi_compile _ DOTS_INSTANCING_ON

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
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
            // BatchRendererGroup draws (DOTS instancing). See TDRPCore.hlsl.
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Includes/Normals.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
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
