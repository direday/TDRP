// Gamma-space UI composition, folded into Present (see PresentPass / TopDownRenderPipeline.SetGammaUiLayer).
// The game renders its UI into a display-res 8-bit UNorm (non-sRGB) layer — a UITK panel with a
// targetTexture and PanelSettings.forceGammaRendering — so the layer holds gamma-encoded,
// premultiplied colors that blended on gamma values, like image editors and CSS. This pass samples
// the final LDR image (linear, from its sRGB buffer), encodes it, composites the layer over it in gamma
// space, and decodes back to linear for the sRGB backbuffer to re-encode. Same single blit as a plain
// Present, plus one texture fetch.
Shader "Hidden/TDRP/UiGammaCompose"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _UiLayer ("UI layer (gamma, premultiplied)", 2D) = "clear" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "ComposeGammaUi"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_UiLayer);
            SAMPLER(sampler_UiLayer);

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

            float4 Frag(Varyings input) : SV_Target
            {
                float3 scene = LinearToSRGB(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb);
                float4 ui = SAMPLE_TEXTURE2D(_UiLayer, sampler_UiLayer, input.uv);
                float3 composed = ui.rgb + scene * (1.0 - ui.a);
                return float4(SRGBToLinear(composed), 1.0);
            }
            ENDHLSL
        }
    }
}
