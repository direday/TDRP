Shader "TDRP/DefaultLit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.8, 0.8, 0.8, 1)
        _Metallic ("Metallic", Range(0, 1)) = 0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
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
        CBUFFER_END
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

            // Compiles main-light shadow sampling in/out (set globally by the pipeline).
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            // Soft-shadow filter (one slot): _SHADOWS_SOFT = 3x3 PCF, _SHADOWS_PCSS =
            // variable-penumbra PCSS, _SHADOWS_PCSS_PYRAMID = PCSS + min/max blocker pyramid,
            // none = single hard tap (driven by the light's Shadow Type + pipeline settings).
            #pragma multi_compile _ _SHADOWS_SOFT _SHADOWS_PCSS _SHADOWS_PCSS_PYRAMID
            // When set, the main shadow was resolved into a full-screen mask this frame and
            // we sample that instead of filtering inline (the soft-filter slot above is then
            // unused here — the mask pass did the filtering). Orthogonal slot, so it composes.
            #pragma multi_compile _ _SCREEN_SPACE_SHADOWS
            // When set, screen-space AO modulates the ambient term (sampled from _ScreenSpaceAO).
            // Opaque only — the transparent shader omits this, so it never applies AO.
            #pragma multi_compile _ _SCREEN_SPACE_AO
            // When set, a world-projected cookie texture modulates the main light's color
            // (cloud shadows / gobo). Set globally by the pipeline when the feature is active.
            #pragma multi_compile _ _MAIN_LIGHT_COOKIE
            // When set, global IBL (split-sum specular + SH diffuse) replaces/augments the hemisphere
            // ambient. Set globally by the pipeline when the env resources are baked.
            #pragma multi_compile _ _ENV_IBL
            // When set, screen-space Radiance Cascades supplies the diffuse indirect (replacing the flat
            // SH/hemisphere ambient). Opaque only — transparents aren't in the depth/normal/color
            // buffers RC reads. Set globally by the pipeline when RC is on AND history exists.
            #pragma multi_compile _ _SCREEN_SPACE_RC

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

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            // Depth-only, like DepthOnly, but rendered from the light's POV.
            // Acne is handled by a sample-time depth bias in Shadows.hlsl (the shader
            // Offset state isn't honored under the native render pass path here), so this
            // pass just writes the caster's depth.
            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

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

            void Frag(Varyings input) { }
            ENDHLSL
        }

        Pass
        {
            // Lays down depth (early-Z for the Lit pass's ZTest Equal) AND the world-space
            // normal into the shared CameraNormals buffer (MRT color target 0). See
            // DepthNormalPrepassRenderPass — this pass is the per-material half of that contract.
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

        Pass
        {
            // Per-object motion vectors. Recomputes this vertex's clip position with the PREVIOUS
            // frame's model matrix (unity_MatrixPreviousM, bound by the engine via
            // PerObjectData.MotionVectors) and the previous camera view-proj (_PrevViewProjMatrix,
            // set by MotionVectorPass), then writes the screen-space delta
            //     motion = currentUV - prevUV
            // exactly as the fullscreen camera-motion pass does — so a STATIC object here reproduces
            // that pass's result, and a MOVING one records its true independent motion.
            //
            // ZTest Equal leans on the depth prepass (only the visible fragment writes), so the
            // current clip position MUST match the prepass's (same TransformObjectToHClip).
            Name "MotionVectors"
            Tags { "LightMode" = "MotionVectors" }

            Cull Back
            ZWrite Off
            ZTest Equal

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            float4x4 _PrevViewProjMatrix;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                // Current + previous clip positions carried through separately: SV_POSITION is
                // consumed by the rasterizer, so we keep our own copies and do the perspective
                // divide in the fragment (both interpolate linearly in clip space — the standard
                // motion-vector technique).
                float4 currClip     : TEXCOORD0;
                float4 prevClip     : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float4 currClip = TransformObjectToHClip(input.positionOS.xyz);
                // Rigid objects only: reuse this vertex's object-space position with the previous
                // model matrix (skinned / vertex-animated meshes would need their previous positions).
                float3 prevWorld = mul(UNITY_PREV_MATRIX_M, float4(input.positionOS.xyz, 1.0)).xyz;
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
