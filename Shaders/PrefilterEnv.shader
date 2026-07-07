Shader "TDRP/PrefilterEnv"
{
    // GGX-prefilters the environment cubemap — the FIRST half of Karis' split-sum. For each output
    // texel (a direction on the cube) at a given roughness, integrate the environment weighted by the
    // GGX lobe centred on that direction (the prefilter assumes the common N = V = R simplification).
    // Rendered per cube face + per mip (the CPU points SetRenderTarget at each face/mip): raster into
    // cube faces sidesteps the "no RWTextureCube UAV" limitation of the compute path. Baked once per
    // environment, then sampled at PerceptualRoughnessToMipmapLevel.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "PrefilterEnv"

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Includes/TDRPCore.hlsl"

            TEXTURECUBE(_SourceCube);
            SAMPLER(sampler_SourceCube);
            float4 _SourceHDRDecode;     // ReflectionProbe.defaultTextureHDRDecodeValues
            float4 _PrefilterParams;     // x: face index, y: roughness, z: sample count, w: unused

            static const float TDRP_PI = 3.14159265358979323846;

            float RadicalInverseVdC(uint bits)
            {
                bits = (bits << 16u) | (bits >> 16u);
                bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
                bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
                bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
                bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
                return float(bits) * 2.3283064365386963e-10;
            }

            float2 Hammersley(uint i, uint n)
            {
                return float2(float(i) / float(n), RadicalInverseVdC(i));
            }

            float3 ImportanceSampleGGXTangent(float2 xi, float roughness)
            {
                float a = roughness * roughness;
                float phi = 2.0 * TDRP_PI * xi.x;
                float cosTheta = sqrt((1.0 - xi.y) / (1.0 + (a * a - 1.0) * xi.y));
                float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
                return float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);
            }

            // (face, uv in [-1,1]) -> world direction, matching the hardware cube-sampling convention
            // so a direction rendered here is retrievable by SampleCube(dir). v is flipped because the
            // render-target row order is top-down relative to the sampling convention (verified via the
            // lat-long debug view — a wrong sign shows the sky upside-down).
            float3 DirectionFromFaceUV(uint face, float2 uv)
            {
                float3 dir;
                if (face == 0)      dir = float3( 1.0, -uv.y, -uv.x); // +X
                else if (face == 1) dir = float3(-1.0, -uv.y,  uv.x); // -X
                else if (face == 2) dir = float3( uv.x,  1.0,  uv.y); // +Y
                else if (face == 3) dir = float3( uv.x, -1.0, -uv.y); // -Y
                else if (face == 4) dir = float3( uv.x, -uv.y,  1.0); // +Z
                else                dir = float3(-uv.x, -uv.y, -1.0); // -Z
                return normalize(dir);
            }

            float3 DecodeHDR(float4 encoded)
            {
                float alpha = max(_SourceHDRDecode.w * (encoded.a - 1.0) + 1.0, 0.0);
                return (_SourceHDRDecode.x * pow(alpha, _SourceHDRDecode.y)) * encoded.rgb;
            }

            float3 SampleSource(float3 dir)
            {
                return DecodeHDR(SAMPLE_TEXTURECUBE_LOD(_SourceCube, sampler_SourceCube, dir, 0));
            }

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
                uint face = (uint)_PrefilterParams.x;
                float roughness = _PrefilterParams.y;
                uint sampleCount = (uint)_PrefilterParams.z;

                float2 uv = input.uv * 2.0 - 1.0;
                float3 N = DirectionFromFaceUV(face, uv);

                // Mirror mip: nothing to integrate, copy the sharp environment.
                if (roughness < 1e-3)
                    return float4(SampleSource(N), 1.0);

                float3 up = abs(N.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
                float3 T = normalize(cross(up, N));
                float3 B = cross(N, T);

                float3 sum = 0.0;
                float weight = 0.0;
                for (uint i = 0u; i < sampleCount; i++)
                {
                    float2 xi = Hammersley(i, sampleCount);
                    float3 Hts = ImportanceSampleGGXTangent(xi, roughness);
                    float3 H = Hts.x * T + Hts.y * B + Hts.z * N;
                    float3 L = 2.0 * dot(N, H) * H - N; // V = N

                    float NdotL = saturate(dot(N, L));
                    if (NdotL > 0.0)
                    {
                        sum += SampleSource(L) * NdotL; // NdotL weighting reduces seams (Karis)
                        weight += NdotL;
                    }
                }
                return float4(sum / max(weight, 1e-4), 1.0);
            }
            ENDHLSL
        }
    }
}
