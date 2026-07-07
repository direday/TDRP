Shader "TDRP/ScreenSpaceShadows"
{
    // Resolves the main directional light's shadow term into a full-screen MASK.
    // Instead of every lit pixel sampling the shadow map inline, we do it once here in
    // screen space; the lit shader then just reads this mask. That indirection is what
    // makes temporal accumulation possible later (a screen buffer can be kept + reprojected
    // across frames). On its own (no temporal) it must look identical to the inline path.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "ScreenSpaceShadows"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            // This shader does the ACTUAL shadow filtering, so it carries the same
            // soft-filter keyword set as the lit shader. SetupLightingPass sets these
            // globally; we deliberately do NOT declare _SCREEN_SPACE_SHADOWS here, so our
            // MainLightShadowAttenuation runs the real inline sample (not a self-referential
            // read of the mask we're writing).
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT _SHADOWS_PCSS _SHADOWS_PCSS_PYRAMID

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Shadows.hlsl"
            #include "Includes/Normals.hlsl"

            // Main light direction (set globally by SetupLightingPass). Shadows.hlsl doesn't
            // declare it — Lighting.hlsl does, but we don't want the cluster machinery here —
            // so declare just this one.
            float4 _MainLightDirection;

            // Inverse view-projection in the GPU clip convention, so we can turn
            // (screen uv, device depth) back into a world position. Set by the pass.
            float4x4 _ScreenToWorld;

            // The camera depth from the prepass, bound as a global by the depth prepass.
            // POINT-sampled: depth must never be linearly blended across a silhouette.
            TEXTURE2D(_CameraDepthTexture);
        #if !defined(TDRP_PCSS)
            // Shadows.hlsl already declares this inline sampler when PCSS is on; declare it
            // here only when it isn't, to avoid a redefinition.
            SAMPLER(sampler_point_clamp);
        #endif

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            // Full-screen triangle generated from the vertex id (no vertex buffer) — the
            // standard SRP fullscreen pass, issued via DrawProcedural(3 verts) in the pass.
            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            float SampleDepth(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_CameraDepthTexture, sampler_point_clamp, uv, 0).r;
            }

            float3 WorldAt(float2 uv, float deviceDepth)
            {
                return ComputeWorldSpacePosition(uv, deviceDepth, _ScreenToWorld);
            }

            float Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float c = SampleDepth(uv);

                // Background pixels (no opaque geometry wrote depth) -> fully lit. The depth
                // prepass cleared to the far value: 0 on reverse-Z, 1 otherwise.
            #if UNITY_REVERSED_Z
                if (c <= 0.0) return 1.0;
            #else
                if (c >= 1.0) return 1.0;
            #endif

                float3 P = WorldAt(uv, c);

                // Normal (used only for the shadow's normal bias) comes from the shared
                // CameraNormals buffer — reconstructed once per frame there instead of
                // re-deriving it per shadow pixel. SV_Position.xy is the exact pixel, so a
                // texel fetch reads the matching normal with no filtering across silhouettes.
                float3 normalWS = LoadSceneNormalWS(uint2(input.positionCS.xy));

                // input.positionCS.xy is SV_Position — feeds the PCSS per-pixel disk rotation,
                // exactly as the inline lit-shader call did.
                return MainLightShadowAttenuation(P, normalWS,
                    normalize(_MainLightDirection.xyz), input.positionCS.xy);
            }
            ENDHLSL
        }
    }
}
