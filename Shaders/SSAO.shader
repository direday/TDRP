Shader "TDRP/SSAO"
{
    // Screen-space ambient occlusion estimate (raw, un-denoised). Normal-oriented hemisphere
    // method, working in WORLD space to reuse the same depth->world reconstruction the shadow
    // passes use. For each pixel: place N cosine-weighted samples in the hemisphere around the
    // surface normal, project each back to screen, and check whether real geometry sits in front
    // of the sample point (i.e. occludes it). A depth range-check fades distant occluders so
    // background silhouettes don't cast halos.
    //
    // Output is a single-channel occlusion factor (1 = fully open, 0 = fully occluded). It's
    // noisy by design — few samples + a per-pixel rotation trade banding for high-frequency noise
    // that the (later) bilateral + temporal stages resolve.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "SSAO"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Normals.hlsl" // _CameraNormalsTexture + LoadSceneNormalWS

            TEXTURE2D(_CameraDepthTexture);
            SAMPLER(sampler_point_clamp);

            float4x4 _ScreenToWorld; // inverse view-projection: (uv, deviceDepth) -> world
            float4x4 _WorldToScreen; // view-projection: world -> clip (to project sample points)
            float4x4 _AoView;        // world -> view, for eye-depth comparisons
            float4 _SsaoParams;      // x: radius (world), y: intensity, z: bias (world), w: unused
            int _SsaoSampleCount;
            float _SsaoFrameJitter;  // per-frame [0,1) offset to the sample rotation (temporal only)

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

            float EyeDepth(float3 posWS)
            {
                return -mul(_AoView, float4(posWS, 1.0)).z;
            }

            // Van der Corput radical inverse (base 2) -> Hammersley low-discrepancy 2D points.
            float RadicalInverseVdC(uint bits)
            {
                bits = (bits << 16u) | (bits >> 16u);
                bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
                bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
                bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
                bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
                return float(bits) * 2.3283064365386963e-10; // / 2^32
            }

            float2 Hammersley(uint i, uint n)
            {
                return float2(float(i) / float(n), RadicalInverseVdC(i));
            }

            // Cosine-weighted hemisphere direction in tangent space (+Z up). Cosine weighting
            // matches the cosine term in the AO integral, so a plain sample average approximates it.
            float3 CosineHemisphere(float2 u)
            {
                float r = sqrt(u.x);
                float phi = 2.0 * PI * u.y;
                return float3(r * cos(phi), r * sin(phi), sqrt(max(0.0, 1.0 - u.x)));
            }

            // Interleaved-gradient noise (Jimenez) — a cheap per-pixel value to decorrelate the
            // sample rotation between neighbours, turning banding into blur-able noise.
            float InterleavedGradientNoise(float2 pix)
            {
                return frac(52.9829189 * frac(dot(pix, float2(0.06711056, 0.00583715))));
            }

            // Output: R = occlusion (1 = open), G = eye depth. The companion depth lets the
            // bilateral blur (and later the temporal resolve) stop at depth discontinuities
            // without re-linearising the depth buffer — same R=value/G=eyeDepth layout the
            // temporal shadow history uses.
            float2 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float c = SampleDepth(uv);
                if (IsBackground(c))
                    return float2(1.0, 0.0); // no geometry -> fully open, depth 0 (blur rejects it)

                float3 P = ComputeWorldSpacePosition(uv, c, _ScreenToWorld);
                // Sample the (full-res) normal by UV, not by pixel coord — this pass may render at
                // half resolution, where SV_Position wouldn't index the full-res normal buffer.
                float3 N = DecodeSceneNormalWS(
                    SAMPLE_TEXTURE2D_LOD(_CameraNormalsTexture, sampler_point_clamp, uv, 0).xyz);
                float centerEye = EyeDepth(P);

                // Build a per-pixel-rotated tangent frame around N. Rotating T/B around N by a
                // noise angle gives each pixel a different sample orientation.
                // Per-frame jitter added so each frame's noise is decorrelated -> temporal
                // accumulation averages it out. Zero (stable) when temporal is off.
                float angle = (InterleavedGradientNoise(input.positionCS.xy) + _SsaoFrameJitter) * 2.0 * PI;
                float3 up = abs(N.z) < 0.99 ? float3(0, 0, 1) : float3(1, 0, 0);
                float3 T = normalize(cross(up, N));
                float3 B = cross(N, T);
                float sn = sin(angle), cs = cos(angle);
                float3 Tr =  T * cs + B * sn;
                float3 Br = -T * sn + B * cs;

                float radius = _SsaoParams.x;
                float bias = _SsaoParams.z;
                uint sampleCount = (uint)max(_SsaoSampleCount, 1);

                float occlusion = 0.0;
                for (uint i = 0; i < sampleCount; i++)
                {
                    float3 h = CosineHemisphere(Hammersley(i, sampleCount));
                    float3 dirWS = Tr * h.x + Br * h.y + N * h.z;

                    // Bias samples toward the centre (more contact detail) — classic SSAO trick.
                    float t = float(i) / float(sampleCount);
                    float scale = lerp(0.1, 1.0, t * t);
                    float3 samplePos = P + dirWS * (radius * scale);

                    // Project the sample point into screen space (mirror the UV_STARTS_AT_TOP flip
                    // that ComputeWorldSpacePosition baked in, see the temporal shadow pass).
                    float4 clip = mul(_WorldToScreen, float4(samplePos, 1.0));
                    float2 ndc = clip.xy / clip.w;
                #if UNITY_UV_STARTS_AT_TOP
                    ndc.y = -ndc.y;
                #endif
                    float2 suv = ndc * 0.5 + 0.5;
                    if (any(suv < 0.0) || any(suv > 1.0))
                        continue;

                    float sd = SampleDepth(suv);
                    if (IsBackground(sd))
                        continue;

                    float3 sceneP = ComputeWorldSpacePosition(suv, sd, _ScreenToWorld);
                    float sampleEye = EyeDepth(samplePos);
                    float sceneEye = EyeDepth(sceneP);

                    // Occluded if the real surface at this screen location is closer to the camera
                    // than the sample point (geometry sits in front of where we sampled).
                    float occluded = sceneEye < sampleEye - bias ? 1.0 : 0.0;

                    // Range check: ignore occluders far from P in depth (kills background halos).
                    float rangeCheck = smoothstep(0.0, 1.0, radius / max(abs(centerEye - sceneEye), 1e-4));
                    occlusion += occluded * rangeCheck;
                }

                float ao = 1.0 - (occlusion / float(sampleCount)) * _SsaoParams.y;
                return float2(saturate(ao), centerEye);
            }
            ENDHLSL
        }
    }
}
