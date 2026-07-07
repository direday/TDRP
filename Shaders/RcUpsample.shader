Shader "TDRP/RcUpsample"
{
    // Joint bilateral upsample of the half-res RC irradiance to full res. Each full-res pixel
    // gathers the 4 nearest half-res texels, weighted by bilinear x depth-similarity x normal-
    // similarity — tap depths/normals are read from the full-res buffers at each tap's centre and
    // compared to the pixel's own, so the upsample never bleeds GI across a depth edge OR a crease.
    //
    // GI is low-frequency, so computing the whole cascade build + resolve at half resolution and
    // upsampling here is ~indistinguishable from full res at roughly a quarter of the cost — the same
    // trade SSAO makes. The exception is a convex CREASE: the two faces receive different bounce, so
    // GI has a real discontinuity there, but depth is continuous across it and the depth term can't
    // see it — the coarse grid bleeds, giving ~2px stairs on the GI-lit face that no post-AA can fix
    // (SMAA reconstructs 1px risers). The normal term rejects across-crease taps, snapping the GI
    // edge to full res. Same fix as SsaoUpsample; mirror any change there here.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "RcUpsample"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"
            #include "Includes/Normals.hlsl" // _CameraNormalsTexture (full-res world normals)

            TEXTURE2D(_RcIrradiance);       // half-res source (global, from resolve/blur)
            TEXTURE2D(_CameraDepthTexture); // full-res device depth (global)
            SAMPLER(sampler_point_clamp);

            float4x4 _ScreenToWorld;    // inverse view-projection (depth -> world)
            float4x4 _RcUpsampleView;   // view (world -> eye depth)
            float4 _RcHalfSize;         // xy: half W,H  zw: 1/halfW,1/halfH

            #define RC_DEPTH_SIGMA_REL 0.05
            #define RC_NORMAL_POWER    8.0 // crease sharpness; matches SsaoUpsample / SsaoBlur

            float3 SampleNormal(float2 uv)
            {
                return normalize(SAMPLE_TEXTURE2D_LOD(_CameraNormalsTexture, sampler_point_clamp, uv, 0).xyz);
            }

            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                o.uv = GetFullScreenTriangleTexCoord(vertexID);
                return o;
            }

            float EyeDepthAtUV(float2 uv)
            {
                float d = SAMPLE_TEXTURE2D_LOD(_CameraDepthTexture, sampler_point_clamp, uv, 0).r;
                return -mul(_RcUpsampleView, float4(ComputeWorldSpacePosition(uv, d, _ScreenToWorld), 1.0)).z;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float centerEye = EyeDepthAtUV(uv);
                float3 n0 = SampleNormal(uv); // full-res normal at this pixel

                // Half-res texel footprint under this full-res pixel.
                float2 hc = uv * _RcHalfSize.xy - 0.5;
                int2 h0 = (int2)floor(hc);
                float2 f = hc - h0;
                float4 bw = float4((1 - f.x) * (1 - f.y), f.x * (1 - f.y), (1 - f.x) * f.y, f.x * f.y);
                int2 hOff[4] = { int2(0,0), int2(1,0), int2(0,1), int2(1,1) };

                float3 sum = 0.0;
                float wsum = 0.0;
                [unroll]
                for (int j = 0; j < 4; j++)
                {
                    int2 h = h0 + hOff[j];
                    float2 hUV = ((float2)h + 0.5) * _RcHalfSize.zw;
                    float he = EyeDepthAtUV(hUV);
                    float dz = (he - centerEye) / (centerEye * RC_DEPTH_SIGMA_REL + 1e-4);
                    // Normal similarity — rejects taps across a crease the depth term can't see.
                    float wNormal = pow(saturate(dot(n0, SampleNormal(hUV))), RC_NORMAL_POWER);
                    float w = bw[j] * exp(-dz * dz) * wNormal;
                    sum += SAMPLE_TEXTURE2D_LOD(_RcIrradiance, sampler_point_clamp, hUV, 0).rgb * w;
                    wsum += w;
                }

                // Fall back to nearest if all taps rejected (deep discontinuity).
                if (wsum < 1e-5)
                    return float4(SAMPLE_TEXTURE2D_LOD(_RcIrradiance, sampler_point_clamp, uv, 0).rgb, 1.0);

                return float4(sum / wsum, 1.0);
            }
            ENDHLSL
        }
    }
}
