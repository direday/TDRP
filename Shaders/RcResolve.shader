Shader "TDRP/RcResolve"
{
    // Resolves the merged cascade 0 into a per-pixel diffuse irradiance (the same _RcIrradiance the M1
    // gather produced, so the downstream blur + apply are unchanged). Cascade 0 holds, per probe, the
    // fully-merged radiance for each of its D0 azimuth directions (near interval + everything the merge
    // continued from the cascades above). For each screen pixel we bilinearly sample the nearest
    // cascade-0 probes, and integrate the directions against the pixel's normal.
    //
    // As in M1 we work in DEVIATION-FROM-AMBIENT: each direction's contribution is (radiance - sky in
    // that direction), so a probe that saw only sky in every direction resolves to exactly the ambient
    // baseline (open areas unchanged), and bounce/occlusion show up as the departure from it. This also
    // sidesteps the 2D gather's blindness to the overhead sky — the sky term comes from ambient(N), not
    // from the horizontal ring.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "RcResolve"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Normals.hlsl" // _CameraNormalsTexture + DecodeSceneNormalWS

            TEXTURE2D(_RcCascade0);
            SAMPLER(sampler_point_clamp);

            float4x4 _RcView;
            float4 _AmbientSkyColor;
            float4 _AmbientGroundColor;
            float4 _RcScreenSize;      // xy: W,H  zw: 1/W,1/H
            float4 _RcResolveParams;   // x: d0, y: s0 (px), z: D0, w: cascade0 texW
            float4 _RcResolveParams2;  // x: cascade0 texH, y: cosine floor, z: intensity
            float4 _RcJitter;          // xy: per-frame sub-cell probe offset (must match the build's)

            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                o.uv = GetFullScreenTriangleTexCoord(vertexID);
                return o;
            }

            float3 SampleAmbient(float3 dir) { return lerp(_AmbientGroundColor.rgb, _AmbientSkyColor.rgb, dir.y * 0.5 + 0.5); }

            float4 Frag(Varyings input) : SV_Target
            {
                int d0 = (int)_RcResolveParams.x;
                float s0 = _RcResolveParams.y;
                int D0 = (int)_RcResolveParams.z;
                int gridW = (int)_RcResolveParams.w / d0;
                int gridH = (int)_RcResolveParams2.x / d0;
                float cosFloor = _RcResolveParams2.y;
                float intensity = _RcResolveParams2.z;

                float2 pixel = input.positionCS.xy;
                float3 N = DecodeSceneNormalWS(
                    SAMPLE_TEXTURE2D_LOD(_CameraNormalsTexture, sampler_point_clamp, input.uv, 0).xyz);
                float3 ambientN = SampleAmbient(N);

                float3 camRight = float3(_RcView._m00, _RcView._m01, _RcView._m02);
                float3 camUp    = float3(_RcView._m10, _RcView._m11, _RcView._m12);

                // Bilinear probe footprint for this pixel. Cascade-0 probe p sits at (p+0.5+jitter)*s0
                // (the build jitters every probe by _RcJitter per frame), so invert with the same offset —
                // else the resolve reads the probes at the wrong screen positions and the jitter, instead
                // of reconstructing a denser grid under temporal, shows up as a wobble.
                float2 pc = pixel / s0 - 0.5 - _RcJitter.xy;
                int2 p0 = (int2)floor(pc);
                float2 f = pc - p0;
                float4 bw = float4((1 - f.x) * (1 - f.y), f.x * (1 - f.y), (1 - f.x) * f.y, f.x * f.y);
                int2 pOff[4] = { int2(0,0), int2(1,0), int2(0,1), int2(1,1) };

                float3 devSum = 0.0;
                float wSum = 1e-4;

                for (int k = 0; k < D0; k++)
                {
                    float theta = (k + 0.5) / (float)D0 * TWO_PI;
                    float3 dirWS = camRight * cos(theta) + camUp * sin(theta);
                    float3 sky = SampleAmbient(dirWS);

                    int kx = k % d0;
                    int ky = k / d0;

                    // Bilinear over the 4 nearest probes for this direction.
                    float3 rad = 0.0;
                    [unroll]
                    for (int j = 0; j < 4; j++)
                    {
                        int2 p = clamp(p0 + pOff[j], int2(0, 0), int2(gridW - 1, gridH - 1));
                        int2 texel = int2(p.x * d0 + kx, p.y * d0 + ky);
                        rad += LOAD_TEXTURE2D(_RcCascade0, texel).rgb * bw[j];
                    }

                    // Cosine weight by the receiver normal (grazing-floored). Deviation from the sky in
                    // this direction is the bounce/occlusion signal.
                    float w = max(dot(dirWS, N), cosFloor);
                    devSum += (rad - sky) * w;
                    wSum += w;
                }

                float3 result = ambientN + (devSum / wSum) * intensity;
                return float4(max(result, 0.0), 1.0);
            }
            ENDHLSL
        }
    }
}
