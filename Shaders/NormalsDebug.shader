Shader "TDRP/NormalsDebug"
{
    // Debug view of the shared world-space normal buffer: maps each normal [-1,1] -> RGB [0,1]
    // (the classic n*0.5+0.5). Lets you eyeball the depth+normal prepass output directly —
    // smoothness (faceted vs interpolated), facing/sign, and coverage. Background texels (no
    // geometry → a zero normal) show black so the geometry pops.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "NormalsDebug"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Normals.hlsl" // declares _CameraNormalsTexture

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // Read the raw stored value (NOT LoadSceneNormalWS, which normalises — that
                // would turn the zero-normal background into a NaN). Check magnitude first.
                float3 raw = LOAD_TEXTURE2D(_CameraNormalsTexture, uint2(input.positionCS.xy)).xyz;
                float len2 = dot(raw, raw);
                if (len2 < 1e-4)
                    return float4(0, 0, 0, 1); // background / no geometry

                float3 n = raw * rsqrt(len2);
                return float4(n * 0.5 + 0.5, 1.0);
            }
            ENDHLSL
        }
    }
}
