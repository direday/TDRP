// DefaultLit for rigid-part-animated meshes: one mesh per unit, each vertex skinned to exactly
// one part matrix from the global palette. Palette contract + data layout: Includes/RigidParts.hlsl
// (C# side: TDRP.PartPalette). Shading is identical to DefaultLit.
Shader "TDRP/RigidPartsLit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.8, 0.8, 0.8, 1)
        _Metallic ("Metallic", Range(0, 1)) = 0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
        // First palette entry of this draw; vertex part indices are relative to it.
        // Per-instance under BatchRendererGroup. A float holding an integer (exact to 2^24).
        _PaletteBase ("Palette Base", Float) = 0
        // Per-instance colour overlays, applied in order over _BaseColor:
        //   albedo = lerp(lerp(_BaseColor, _TintColor.rgb, _TintColor.a), _FlashColor.rgb, _FlashColor.a)
        // so every submesh keeps its own material colour at rest (e.g. team tint, hit flash).
        _TintColor ("Tint (RGB target, A amount)", Color) = (0, 0, 0, 0)
        _FlashColor ("Flash (RGB target, A amount)", Color) = (0, 0, 0, 0)
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
        #include "Includes/Lighting.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float _Metallic;
            float _Smoothness;
            float _PaletteBase;
            float4 _TintColor;
            float4 _FlashColor;
        CBUFFER_END

        // Per-instance overrides for BatchRendererGroup draws. A property the BRG does not
        // supply falls back to the material value.
        #ifdef UNITY_DOTS_INSTANCING_ENABLED
        UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
            UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
            UNITY_DOTS_INSTANCED_PROP(float, _Metallic)
            UNITY_DOTS_INSTANCED_PROP(float, _Smoothness)
            UNITY_DOTS_INSTANCED_PROP(float, _PaletteBase)
            UNITY_DOTS_INSTANCED_PROP(float4, _TintColor)
            UNITY_DOTS_INSTANCED_PROP(float4, _FlashColor)
        UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

        #define _BaseColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor)
        #define _Metallic UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _Metallic)
        #define _Smoothness UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _Smoothness)
        #define _PaletteBase UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _PaletteBase)
        #define _TintColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _TintColor)
        #define _FlashColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _FlashColor)
        #endif

        #include "Includes/RigidParts.hlsl"

        // Every pass derives world position the same way, so the depth prepass and the
        // ZTest Equal passes (Lit, MotionVectors) produce bit-identical clip positions.
        float3 RigidPartsPositionWS(float3 positionPS, float partIndex)
        {
            return PartToWorld(LoadPartMatrix(GetPartPaletteIndex(partIndex)), positionPS);
        }
        ENDHLSL

        Pass
        {
            Name "Lit"
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

            // Same global lighting slots as DefaultLit (see there for what each one does).
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT _SHADOWS_PCSS _SHADOWS_PCSS_PYRAMID
            #pragma multi_compile _ _SCREEN_SPACE_SHADOWS
            #pragma multi_compile _ _SCREEN_SPACE_AO
            #pragma multi_compile _ _MAIN_LIGHT_COOKIE
            #pragma multi_compile _ _ENV_IBL
            #pragma multi_compile _ _SCREEN_SPACE_RC

            struct Attributes
            {
                float4 positionPS : POSITION;
                float3 normalPS : NORMAL;
                float partIndex : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                float3x4 part = LoadPartMatrix(GetPartPaletteIndex(input.partIndex));
                output.positionWS = RigidPartsPositionWS(input.positionPS.xyz, input.partIndex);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = PartToWorldNormal(part, input.normalPS);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                SurfaceData surface;
                float4 tint = _TintColor;
                float4 flash = _FlashColor;
                surface.albedo = lerp(lerp(_BaseColor.rgb, tint.rgb, tint.a), flash.rgb, flash.a);
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

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            // Depth-only from the light's POV; bias is applied at sample time (see DefaultLit).
            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            // BatchRendererGroup draws (DOTS instancing). See TDRPCore.hlsl.
            #pragma multi_compile _ DOTS_INSTANCING_ON

            struct Attributes
            {
                float4 positionPS : POSITION;
                float partIndex : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionCS = TransformWorldToHClip(RigidPartsPositionWS(input.positionPS.xyz, input.partIndex));
                return output;
            }

            void Frag(Varyings input) { }
            ENDHLSL
        }

        Pass
        {
            // Depth + world normal prepass (see DefaultLit / DepthNormalPrepassRenderPass).
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
                float4 positionPS : POSITION;
                float3 normalPS : NORMAL;
                float partIndex : TEXCOORD3;
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
                float3x4 part = LoadPartMatrix(GetPartPaletteIndex(input.partIndex));
                output.positionCS = TransformWorldToHClip(RigidPartsPositionWS(input.positionPS.xyz, input.partIndex));
                output.normalWS = PartToWorldNormal(part, input.normalPS);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                return float4(EncodeSceneNormalWS(normalize(input.normalWS)), 0.0);
            }
            ENDHLSL
        }

        Pass
        {
            // Per-object motion vectors, as in DefaultLit, but the previous position comes from
            // LAST frame's palette entry for this vertex's part (_PartPalettePrev) rather than the
            // renderer's previous model matrix — so an animated part records its own motion even
            // when the unit as a whole stands still.
            Name "MotionVectors"
            Tags { "LightMode" = "MotionVectors" }

            Cull Back
            ZWrite Off
            ZTest Equal

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            // BatchRendererGroup draws (DOTS instancing). See TDRPCore.hlsl.
            #pragma multi_compile _ DOTS_INSTANCING_ON

            float4x4 _PrevViewProjMatrix;

            struct Attributes
            {
                float4 positionPS : POSITION;
                float partIndex : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float4 currClip     : TEXCOORD0;
                float4 prevClip     : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                float4 currClip = TransformWorldToHClip(RigidPartsPositionWS(input.positionPS.xyz, input.partIndex));
                float3 prevWorld = PartToWorld(LoadPrevPartMatrix(GetPartPaletteIndex(input.partIndex)), input.positionPS.xyz);
                float4 prevClip = mul(_PrevViewProjMatrix, float4(prevWorld, 1.0));

                output.positionCS = currClip;
                output.currClip = currClip;
                output.prevClip = prevClip;
                return output;
            }

            float2 ClipToUV(float4 clip)
            {
                float2 ndc = clip.xy / clip.w;
            #if UNITY_UV_STARTS_AT_TOP
                ndc.y = -ndc.y; // same render-target-uv convention as the camera-motion pass
            #endif
                return ndc * 0.5 + 0.5;
            }

            float2 Frag(Varyings input) : SV_Target
            {
                return ClipToUV(input.currClip) - ClipToUV(input.prevClip);
            }
            ENDHLSL
        }
    }
}
