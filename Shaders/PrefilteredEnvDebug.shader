Shader "TDRP/PrefilteredEnvDebug"
{
    // Debug view of the GGX-prefiltered environment cube, unwrapped to an equirectangular (lat-long)
    // image so the whole environment is visible independent of the camera. The mip is chosen from
    // ShowPrefilteredEnvRoughness, so sweeping that knob shows the environment blur from mirror-sharp
    // to fully rough.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "PrefilteredEnvDebug"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURECUBE(_PrefilteredEnvTex);
            SAMPLER(sampler_PrefilteredEnvTex);
            float4 _PrefilteredEnvDebugParams; // x: mip level to sample

            static const float TDRP_PI = 3.14159265358979323846;

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

            float4 Frag(Varyings input) : SV_Target
            {
                // Lat-long: x -> azimuth [-PI,PI], y (top=up) -> polar [0,PI].
                float phi = (input.uv.x * 2.0 - 1.0) * TDRP_PI;
                float theta = (1.0 - input.uv.y) * TDRP_PI;
                float sinT = sin(theta);
                float3 dir = float3(sinT * sin(phi), cos(theta), sinT * cos(phi));

                float3 col = SAMPLE_TEXTURECUBE_LOD(_PrefilteredEnvTex, sampler_PrefilteredEnvTex,
                    dir, _PrefilteredEnvDebugParams.x).rgb;
                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
