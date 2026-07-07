Shader "TDRP/MotionVectors"
{
    // Fullscreen CAMERA motion vectors, reconstructed from depth. For each pixel: reconstruct the
    // world position from the current depth + inverse view-proj, project it through the PREVIOUS
    // frame's view-proj, and store the screen-space delta as
    //     motion = currentUV - prevUV      (so prevUV = currentUV - motion, Unity's convention).
    // This covers all static geometry and the camera-induced component of everything. Moving objects
    // then overwrite their pixels via their own MotionVectors pass (MotionVectorPass, pass 2).
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "CameraMotionVectors"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURE2D(_CameraDepthTexture); // current device depth (global, from the prepass)
            SAMPLER(sampler_point_clamp);

            float4x4 _PrevViewProjMatrix;     // last frame's view-projection (reproject into its screen)
            float4x4 _CurrInvViewProjMatrix;  // current inverse view-projection (depth -> world)
            float4 _MotionVectorParams;       // x: hasHistory (0/1)

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

            float2 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;

                // No usable history yet (first frame / camera just appeared): zero motion.
                if (_MotionVectorParams.x < 0.5)
                    return float2(0.0, 0.0);

                float deviceDepth = SAMPLE_TEXTURE2D_LOD(_CameraDepthTexture, sampler_point_clamp, uv, 0).r;
                // Note: background/sky sits at the cleared far-plane depth. With a consistent
                // previous view-proj this still reprojects correctly (sky slides under camera
                // rotation, which is what we want for temporal reprojection); no special-casing
                // needed. Only a mismatched prev VP (e.g. an out-of-band editor re-render at a
                // different aspect) makes the far plane amplify into visible noise.
                float3 worldPos = ComputeWorldSpacePosition(uv, deviceDepth, _CurrInvViewProjMatrix);

                float4 prevClip = mul(_PrevViewProjMatrix, float4(worldPos, 1.0));
                float2 prevNdc = prevClip.xy / prevClip.w;
            #if UNITY_UV_STARTS_AT_TOP
                // Mirror the Y-flip ComputeWorldSpacePosition baked in, so the reprojected UV lands
                // in the same convention as the current uv (else the Y delta is inverted).
                prevNdc.y = -prevNdc.y;
            #endif
                float2 prevUV = prevNdc * 0.5 + 0.5;

                return uv - prevUV;
            }
            ENDHLSL
        }
    }
}
