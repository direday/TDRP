Shader "TDRP/RadianceCascades"
{
    // Screen-space Radiance Cascades — Milestone 1: a SINGLE cascade (no hierarchy, no merging yet).
    // Produces a per-pixel diffuse indirect irradiance that the lit shader uses in place of the flat
    // SH/hemisphere ambient, so nearby lit/emissive surfaces bleed their color onto neighbours.
    //
    // HOW IT WORKS (the top-down 2D approximation):
    //   For each shaded pixel we cast N rays in the SCREEN PLANE (evenly spaced around 2*pi, plus a
    //   per-pixel rotation). Each ray marches OUTWARD in world space along a direction that lies in the
    //   plane parallel to the image plane through the surface point P (spanned by the camera's world
    //   right/up axes). At each step we project back to screen and read the scene depth: because the
    //   camera looks down, a scene surface that is CLOSER to the camera than P (smaller eye depth) is a
    //   TALLER object — i.e. an occluder rising above P's ground level. The first such occluder along
    //   the ray is the "hit": we sample last frame's HDR color there as the radiance arriving from that
    //   direction, cosine-weight it by the real geometric direction to the hit, and stop that ray.
    //   Rays that reach the max distance without hitting anything "miss" and see the ambient sky.
    //
    // WHY PREVIOUS-FRAME COLOR: in a forward pipeline there is no lit color available mid-frame, so the
    //   gather reads the finished HDR color from LAST frame (bound as _RcPrevColor). This gives emissive
    //   surfaces + multi-bounce for free; the bounce self-attenuates because the lit shader multiplies
    //   the result by albedo * kD (< 1).
    //
    // HONEST M1 LIMITATIONS (each addressed in a later milestone):
    //   * The "closer-to-camera = taller = occluder" test assumes a roughly top-down camera; it is not a
    //     general hemisphere gather (that's the point — it's the cheap 2D RC domain). Two coplanar ground
    //     patches never occlude each other, so flat-to-flat bleed is zero by design; bleed comes from
    //     RAISED geometry (walls, objects). A normal-oriented hemisphere variant is a future option.
    //   * No cascade hierarchy => reach is limited to _RcMaxDistance and cost is rayCount*stepCount per
    //     pixel (full-res). The hierarchy (spacing x2 / rays x4, cheap long range) is Milestone 2.
    //   * No temporal accumulation / denoise => the per-pixel ray rotation shows as noise near partial
    //     occlusion. Raise the ray count for M1; the TemporalHistory + bilateral infra generalises next.
    //   * History is sampled at the current hit UV (no reprojection) => a little smear under fast camera
    //     motion. Fine for a near-static top-down view; prev-VP reprojection is a later add.
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "RadianceCascades"

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

            // Previous frame's finished HDR color, bound directly on this material by the pass (a
            // persistent RTHandle exists at record time). Sampled with a linear-clamp sampler because
            // the hit UV is generally sub-texel.
            TEXTURE2D(_RcPrevColor);
            SAMPLER(sampler_linear_clamp);

            float4x4 _ScreenToWorld; // inverse view-projection: (uv, deviceDepth) -> world
            float4x4 _WorldToScreen; // view-projection: world -> clip (to project marched points)
            float4x4 _RcView;        // world -> view, for eye-depth comparisons AND camera basis (its
                                     // rows are the world-space camera right/up/-forward axes)

            float4 _RcParams;  // x: ray count, y: steps per ray, z: max distance (world), w: intensity
            float4 _RcParams2; // x: depth bias (world), y: cosine floor, z: per-frame jitter, w: sky floor

            // Hemisphere ambient (linear, intensity-folded) — the same globals SetupLightingPass binds
            // for the lit shader. Used as the miss radiance AND the open-area baseline, so an all-miss
            // probe reproduces exactly the ambient the lit shader would show without RC (open areas
            // stay unchanged). Redeclared here to avoid pulling in the whole Lighting.hlsl stack.
            float4 _AmbientSkyColor;
            float4 _AmbientGroundColor;

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
                return -mul(_RcView, float4(posWS, 1.0)).z;
            }

            // Same hemisphere blend the lit shader uses (sky up, ground down, smooth through horizon).
            float3 SampleAmbient(float3 normalWS)
            {
                return lerp(_AmbientGroundColor.rgb, _AmbientSkyColor.rgb, normalWS.y * 0.5 + 0.5);
            }

            // Interleaved-gradient noise (Jimenez): a cheap per-pixel value to rotate each pixel's ray
            // fan, turning direction banding into (blur-able / temporally-resolvable) noise.
            float InterleavedGradientNoise(float2 pix)
            {
                return frac(52.9829189 * frac(dot(pix, float2(0.06711056, 0.00583715))));
            }

            // Project a world point to screen UV, mirroring the UNITY_UV_STARTS_AT_TOP flip that
            // ComputeWorldSpacePosition bakes into the inverse direction (same as SSAO / temporal).
            float2 WorldToScreenUV(float3 posWS, out bool onScreen)
            {
                float4 clip = mul(_WorldToScreen, float4(posWS, 1.0));
                float2 ndc = clip.xy / clip.w;
            #if UNITY_UV_STARTS_AT_TOP
                ndc.y = -ndc.y;
            #endif
                float2 uv = ndc * 0.5 + 0.5;
                onScreen = !(any(uv < 0.0) || any(uv > 1.0));
                return uv;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float c = SampleDepth(uv);

                float3 P = ComputeWorldSpacePosition(uv, c, _ScreenToWorld);
                // UV sample (not LOAD by pixel): keeps this correct if the pass is ever run at a
                // different resolution than the full-res normal buffer.
                float3 N = DecodeSceneNormalWS(
                    SAMPLE_TEXTURE2D_LOD(_CameraNormalsTexture, sampler_point_clamp, uv, 0).xyz);

                // Baseline = the ambient this surface would get with RC off. Background pixels just get
                // it too (they aren't shaded as opaque, so the value is irrelevant there).
                float3 ambientN = SampleAmbient(N);
                if (IsBackground(c))
                    return float4(ambientN, 1.0);

                float centerEye = EyeDepth(P);

                // World-space camera basis spanning the screen plane: rows of the world->view matrix are
                // the camera's right / up / -forward axes in world space. Marching in (right, up) keeps
                // the ray in the image-parallel plane through P (so its eye depth stays ~= centerEye,
                // and any closer scene surface is genuinely a taller occluder).
                float3 camRight = float3(_RcView._m00, _RcView._m01, _RcView._m02);
                float3 camUp    = float3(_RcView._m10, _RcView._m11, _RcView._m12);

                int rayCount = (int)max(_RcParams.x, 1.0);
                int stepCount = (int)max(_RcParams.y, 1.0);
                float maxDist = _RcParams.z;
                float bias = _RcParams2.x;
                float cosFloor = _RcParams2.y;
                float skyFloor = _RcParams2.w;

                // Per-pixel + per-frame rotation of the whole ray fan.
                float baseAngle = (InterleavedGradientNoise(input.positionCS.xy) + _RcParams2.z) * TWO_PI;
                float angleStep = TWO_PI / float(rayCount);

                float3 gathered = float3(0.0, 0.0, 0.0);
                float weightSum = 1e-4;

                for (int r = 0; r < rayCount; r++)
                {
                    float theta = baseAngle + angleStep * float(r);
                    float3 dirWS = camRight * cos(theta) + camUp * sin(theta);

                    float3 rad = ambientN; // miss => this surface's ambient (open areas stay unchanged)
                    float w = 1.0;         // per-ray weight (updated on hit)

                    for (int s = 1; s <= stepCount; s++)
                    {
                        float t = float(s) / float(stepCount);
                        float dist = maxDist * t;
                        float3 marchWS = P + dirWS * dist;

                        bool onScreen;
                        float2 suv = WorldToScreenUV(marchWS, onScreen);
                        if (!onScreen)
                            break;

                        float sd = SampleDepth(suv);
                        if (IsBackground(sd))
                            continue;

                        float3 sceneP = ComputeWorldSpacePosition(suv, sd, _ScreenToWorld);
                        float sceneEye = EyeDepth(sceneP);
                        float marchEye = EyeDepth(marchWS);

                        // Occluder: the real surface here is closer to the camera than the ray = taller
                        // geometry rising above P. That surface is what lights P from this direction.
                        if (sceneEye < marchEye - bias)
                        {
                            float3 toHit = sceneP - P;
                            float hitDist = length(toHit);
                            // Cosine term via the true 3D direction to the hit (an elevated emitter gives
                            // a positive dot); a small floor keeps a faint contribution alive rather than
                            // hard-zeroing grazing hits.
                            float cosW = max(dot(toHit / max(hitDist, 1e-4), N), cosFloor);
                            // Distance falloff so far occluders bleed less than close ones.
                            float falloff = saturate(1.0 - hitDist / max(maxDist, 1e-4));
                            float3 hitColor = SAMPLE_TEXTURE2D_LOD(_RcPrevColor, sampler_linear_clamp, suv, 0).rgb;
                            // Sky-visibility floor: a horizontal hit only blocks the HORIZON of this
                            // azimuth wedge, not the sky higher up in it (the 2D gather can't see the
                            // upper hemisphere). Leak skyFloor of the ambient back through every hit so
                            // shadowed-but-open-above ground doesn't collapse to the neighbours' darkness;
                            // it also correctly tempers bright bleed toward ambient. 0 = full 2D occlusion
                            // (can darken hard), 1 = hits ignored (never darkens below ambient).
                            rad = lerp(hitColor, ambientN, skyFloor);
                            w = cosW * falloff;
                            break;
                        }
                    }

                    gathered += rad * w;
                    weightSum += w;
                }

                float3 avg = gathered / weightSum;

                // Intensity scales the DEVIATION from the ambient baseline, not the baseline itself: at
                // intensity 1 an open probe (avg == ambientN) is unchanged; >1 punches up local bleed;
                // 0 collapses to plain ambient. This keeps open areas invariant to the knob.
                float3 result = ambientN + (avg - ambientN) * _RcParams.w;
                return float4(max(result, 0.0), 1.0);
            }
            ENDHLSL
        }
    }
}
