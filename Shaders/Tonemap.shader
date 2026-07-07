Shader "TDRP/Tonemap"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "Tonemap"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ _TONEMAP_REINHARD _TONEMAP_ACES

            #include "Includes/TDRPCore.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            float3 ReinhardTonemap(float3 c)
            {
                return c / (1.0 + c);
            }

            float3 AcesFilmicTonemap(float3 x)
            {
                const float a = 2.51;
                const float b = 0.03;
                const float c = 2.43;
                const float d = 0.59;
                const float e = 0.14;
                return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float3 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb;

                #if defined(_TONEMAP_ACES)
                    color = AcesFilmicTonemap(color);
                #elif defined(_TONEMAP_REINHARD)
                    color = ReinhardTonemap(color);
                #else
                    color = saturate(color);
                #endif

                return float4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
