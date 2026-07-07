Shader "TDRP/DefaultLitTransparent"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.8, 0.8, 0.8, 0.5)
        _Metallic ("Metallic", Range(0, 1)) = 0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "TopDownRenderPipeline"
            // Queue "Transparent" puts this material in RenderQueueRange.transparent,
            // so the transparent pass collects it and the opaque pass skips it.
            "Queue" = "Transparent"
        }

        HLSLINCLUDE
        #include "Includes/TDRPCore.hlsl"
        #include "Includes/Lighting.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float _Metallic;
            float _Smoothness;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "LitTransparent"
            Tags { "LightMode" = "TDRPTransparent" }

            // Straight-alpha over: out = src.rgb * src.a + dst * (1 - src.a).
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Back
            // ZTest LEqual (not Equal): transparents have no depth-prepass entry, so
            // they test against the opaque depth to be occluded correctly.
            // ZWrite Off: they don't occlude each other — the back-to-front sort does.
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            // Receive the main-light shadow (transparents don't cast — no ShadowCaster pass).
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT _SHADOWS_PCSS _SHADOWS_PCSS_PYRAMID
            // Cloud/gobo cookie also darkens transparents (they read the same main light).
            #pragma multi_compile _ _MAIN_LIGHT_COOKIE
            #pragma multi_compile _ _ENV_IBL

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                SurfaceData surface;
                surface.albedo = _BaseColor.rgb;
                surface.metallic = _Metallic;
                surface.smoothness = _Smoothness;
                surface.normalWS = normalize(input.normalWS);
                surface.viewDirWS = _WorldSpaceCameraPos - input.positionWS;
                surface.positionWS = input.positionWS;

                float3 color = ShadeSurface(surface, input.positionCS.xy);
                return float4(color, _BaseColor.a);
            }
            ENDHLSL
        }

        // Weighted Blended OIT accumulation pass. Selected by the pipeline (instead of the
        // LitTransparent pass above) when TransparencyMode = WeightedBlended. Same shading, but
        // every fragment is written ORDER-INDEPENDENTLY into two MRT buffers that a later
        // composite pass resolves — so no back-to-front sort is needed.
        Pass
        {
            Name "LitTransparentOit"
            Tags { "LightMode" = "TDRPTransparentOit" }

            // Per-attachment blend (MRT). Target 0 (accum, RGBA16F) sums contributions
            // additively; target 1 (revealage, R8, cleared to 1) multiplies by (1 - alpha),
            // i.e. dst' = dst * (1 - src). Draw order can't change either result.
            Blend 0 One One
            Blend 1 Zero OneMinusSrcColor
            Cull Back
            // Same depth contract as the sorted path: tested against opaque depth (occlusion),
            // but never written (transparents don't occlude each other here).
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT _SHADOWS_PCSS _SHADOWS_PCSS_PYRAMID
            #pragma multi_compile _ _MAIN_LIGHT_COOKIE
            #pragma multi_compile _ _ENV_IBL

            // Shared WBOIT helpers: the OitFragOutput MRT struct + the depth weight + OutputOit().
            #include "Includes/OIT.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                // Positive linear view-space depth, fed to the OIT weight. View space is
                // platform-independent (unlike reversed-Z window depth) and linear, which the
                // 4th-power weight wants.
                float viewDepth : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDepth = -TransformWorldToView(output.positionWS).z;
                return output;
            }

            OitFragOutput Frag(Varyings input)
            {
                SurfaceData surface;
                surface.albedo = _BaseColor.rgb;
                surface.metallic = _Metallic;
                surface.smoothness = _Smoothness;
                surface.normalWS = normalize(input.normalWS);
                surface.viewDirWS = _WorldSpaceCameraPos - input.positionWS;
                surface.positionWS = input.positionWS;

                float3 color = ShadeSurface(surface, input.positionCS.xy);
                return OutputOit(color, _BaseColor.a, input.viewDepth);
            }
            ENDHLSL
        }
    }
}
