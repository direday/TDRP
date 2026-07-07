Shader "TDRP/GTAO"
{
    // Ground-Truth Ambient Occlusion (Jimenez et al. 2016). An alternative AO estimator to the
    // hemisphere SSAO, producing the SAME output buffer (R = occlusion, G = eye depth) so the
    // rest of the chain (bilateral blur, temporal, upsample, composite) is unchanged.
    //
    // Idea: instead of counting occluded hemisphere samples, GTAO works in VIEW space and, for a
    // few "slices" (screen-space directions around the pixel), marches each side to find the
    // HORIZON angle (the steepest occluder), then evaluates the analytic cosine-weighted
    // visibility arc between the two horizons. Summed over slices this converges to the reference
    // AO integral — sharper and more accurate at contacts than the biased hemisphere count.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "GTAO"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Normals.hlsl" // _CameraNormalsTexture + DecodeSceneNormalWS

            TEXTURE2D(_CameraDepthTexture);
            SAMPLER(sampler_point_clamp);

            float4x4 _ScreenToWorld; // inverse view-projection: (uv, deviceDepth) -> world
            float4x4 _AoView;        // world -> view
            float4 _SsaoParams;      // x: radius (world), y: intensity (power), z: bias, w: unused
            float _SsaoFrameJitter;  // per-frame [0,1) offset to the slice rotation (temporal only)
            float4 _GtaoParams;      // x: slice count, y: steps/slice, z: |proj m00|, w: |proj m11|

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

            float SampleDepth(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_CameraDepthTexture, sampler_point_clamp, uv, 0).r;
            }

            bool IsBackground(float deviceDepth)
            {
            #if UNITY_REVERSED_Z
                return deviceDepth <= 0.0;
            #else
                return deviceDepth >= 1.0;
            #endif
            }

            // View-space position from (uv, device depth).
            float3 ViewPos(float2 uv, float deviceDepth)
            {
                float3 wp = ComputeWorldSpacePosition(uv, deviceDepth, _ScreenToWorld);
                return mul(_AoView, float4(wp, 1.0)).xyz;
            }

            float InterleavedGradientNoise(float2 pix)
            {
                return frac(52.9829189 * frac(dot(pix, float2(0.06711056, 0.00583715))));
            }

            float2 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float c = SampleDepth(uv);
                if (IsBackground(c))
                    return float2(1.0, 0.0); // no geometry -> fully open

                float3 P = ViewPos(uv, c);
                float eyeDepth = -P.z;
                // World normal -> view space (rigid view matrix, so the 3x3 is fine, no inv-transpose).
                float3 nWS = DecodeSceneNormalWS(
                    SAMPLE_TEXTURE2D_LOD(_CameraNormalsTexture, sampler_point_clamp, uv, 0).xyz);
                float3 N = normalize(mul((float3x3)_AoView, nWS));
                float3 V = normalize(-P); // toward the camera (origin in view space)

                float radius = _SsaoParams.x;
                int sliceCount = max(1, (int)_GtaoParams.x);
                int steps = max(1, (int)_GtaoParams.y);

                // World radius projected to a uv march extent at this depth. Clamp so we don't
                // sweep half the screen on very close surfaces.
                float2 radiusUV = 0.5 * radius * _GtaoParams.zw / max(eyeDepth, 1e-3);
                radiusUV = min(radiusUV, 0.25);

                float noise = frac(InterleavedGradientNoise(input.positionCS.xy) + _SsaoFrameJitter);

                // HALF_PI is provided by the core Common.hlsl.
                float visibility = 0.0;

                [loop]
                for (int slice = 0; slice < sliceCount; slice++)
                {
                    // Slices span [0, PI) — each covers two opposite march directions.
                    float phi = (float(slice) + noise) * (PI / float(sliceCount));
                    float2 dir = float2(cos(phi), sin(phi));
                    float2 stepUV = dir * radiusUV / float(steps);

                    // View-space slice direction (unproject a small step at the centre depth), and
                    // the in-plane tangent T perpendicular to V (the +omega direction).
                    float3 sliceP = ViewPos(uv + dir * radiusUV * 0.02, c);
                    float3 sliceDir = normalize(sliceP - P);
                    float3 T = normalize(sliceDir - V * dot(sliceDir, V));

                    // Project N onto the slice plane (perp to planeN = V x sliceDir); gamma is its
                    // signed angle from V (positive toward +omega).
                    float3 planeN = normalize(cross(sliceDir, V));
                    float3 projN = N - planeN * dot(N, planeN);
                    float projLen = length(projN);
                    if (projLen < 1e-4)
                        continue;
                    float3 projNn = projN / projLen;
                    float gamma = atan2(dot(projNn, T), dot(projNn, V));

                    // March both sides; track the highest horizon (max cos), with distance falloff.
                    float cosH1 = -1.0; // -omega side
                    float cosH2 = -1.0; // +omega side
                    [loop]
                    for (int s = 1; s <= steps; s++)
                    {
                        float2 o = stepUV * float(s);

                        float2 uvp = uv + o;
                        float dp = SampleDepth(uvp);
                        if (!IsBackground(dp))
                        {
                            float3 dv = ViewPos(uvp, dp) - P;
                            float dist = length(dv);
                            float ch = dot(dv / max(dist, 1e-5), V);
                            cosH2 = max(cosH2, lerp(-1.0, ch, saturate(1.0 - dist / radius)));
                        }

                        float2 uvm = uv - o;
                        float dm = SampleDepth(uvm);
                        if (!IsBackground(dm))
                        {
                            float3 dv = ViewPos(uvm, dm) - P;
                            float dist = length(dv);
                            float ch = dot(dv / max(dist, 1e-5), V);
                            cosH1 = max(cosH1, lerp(-1.0, ch, saturate(1.0 - dist / radius)));
                        }
                    }

                    // Signed horizon angles, clamped to the hemisphere around the normal (can't see
                    // behind the surface).
                    float h1 = -acos(clamp(cosH1, -1.0, 1.0));
                    float h2 =  acos(clamp(cosH2, -1.0, 1.0));
                    h1 = gamma + max(h1 - gamma, -HALF_PI);
                    h2 = gamma + min(h2 - gamma,  HALF_PI);

                    // Analytic cosine-weighted visibility arc (the GTAO inner integral), weighted by
                    // the projected-normal length.
                    float a1 = 0.25 * (-cos(2.0 * h1 - gamma) + cos(gamma) + 2.0 * h1 * sin(gamma));
                    float a2 = 0.25 * (-cos(2.0 * h2 - gamma) + cos(gamma) + 2.0 * h2 * sin(gamma));
                    visibility += projLen * (a1 + a2);
                }

                visibility = saturate(visibility / float(sliceCount));
                float ao = pow(visibility, max(_SsaoParams.y, 1e-3)); // intensity as a power
                return float2(ao, eyeDepth);
            }
            ENDHLSL
        }
    }
}
