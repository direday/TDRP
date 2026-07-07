Shader "TDRP/MotionVectorsDebug"
{
    // Debug view of _CameraMotionVectorsTexture. Encodes each pixel's screen-space velocity as a
    // flow map: DIRECTION -> hue (a color wheel), MAGNITUDE -> brightness. Still pixels read black,
    // so anything moving on screen (from camera motion or object motion) lights up, colored by which
    // way it slid. Lets you eyeball coverage and sign directly.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "MotionVectorsDebug"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURE2D(_CameraMotionVectorsTexture);

            // Motion in UV space is small (a fast pan is a few percent of the screen per frame), so
            // scale magnitude up to make it visible. Raise for slow motion, lower if it blows out.
            #define MOTION_DEBUG_INTENSITY 40.0

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

            // Hue [0,1] -> RGB (full saturation), no external color-library dependency.
            float3 HueToRGB(float h)
            {
                float r = abs(h * 6.0 - 3.0) - 1.0;
                float g = 2.0 - abs(h * 6.0 - 2.0);
                float b = 2.0 - abs(h * 6.0 - 4.0);
                return saturate(float3(r, g, b));
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 motion = LOAD_TEXTURE2D(_CameraMotionVectorsTexture, uint2(input.positionCS.xy)).xy;

                float mag = length(motion);
                if (mag < 1e-6)
                    return float4(0, 0, 0, 1); // still -> black

                float hue = atan2(motion.y, motion.x) / TWO_PI + 0.5; // direction -> color wheel
                float value = saturate(mag * MOTION_DEBUG_INTENSITY);  // speed -> brightness
                return float4(HueToRGB(hue) * value, 1.0);
            }
            ENDHLSL
        }
    }
}
