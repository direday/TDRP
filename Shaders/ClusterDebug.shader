Shader "TDRP/ClusterDebug"
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
            Name "ClusterDebug"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Clustering.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            StructuredBuffer<uint> _ClusterLightGrid;
            float4 _ClusterScreenDimensions; // xy: render target width/height in pixels

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

            // Blue (cold) -> green -> red (hot).
            float3 HeatColor(float t)
            {
                float3 cold = float3(0, 0, 1);
                float3 mid  = float3(0, 1, 0);
                float3 hot  = float3(1, 0, 0);
                return t < 0.5 ? lerp(cold, mid, t * 2.0) : lerp(mid, hot, t * 2.0 - 1.0);
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float3 scene = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb;

                // Use SV_Position (reliably top-left, y-down in Unity) so the tile we
                // sample matches exactly how the compute pass partitioned the screen.
                float2 screenUV = input.positionCS.xy / _ClusterScreenDimensions.xy;
                uint2 tile = (uint2)floor(screenUV * float2(CLUSTER_X, CLUSTER_Y));
                tile = min(tile, uint2(CLUSTER_X - 1, CLUSTER_Y - 1));

                // This 2D heatmap collapses the depth slices: show the busiest cluster
                // in this screen tile's column. (Per-pixel depth lookup comes when the
                // lit shader consumes the grid for real.)
                uint maxCount = 0;
                for (uint z = 0; z < (uint)CLUSTER_Z; z++)
                {
                    uint idx = ClusterLinearIndex(uint3(tile, z));
                    maxCount = max(maxCount, _ClusterLightGrid[idx]);
                }

                float t = saturate(maxCount / 8.0); // 8+ lights in a cluster = full red
                float3 heat = HeatColor(t);
                float overlay = maxCount > 0 ? 0.65 : 0.0; // leave empty tiles untouched

                return float4(lerp(scene, heat, overlay), 1.0);
            }
            ENDHLSL
        }
    }
}
