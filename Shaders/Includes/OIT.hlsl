#ifndef TDRP_OIT_INCLUDED
#define TDRP_OIT_INCLUDED

// Weighted Blended OIT (McGuire-Bavoil 2013) — shared helpers for the accumulation pass.
//
// Any transparent shader that should participate in the order-independent path adds a pass:
//
//     Pass
//     {
//         Tags { "LightMode" = "TDRPTransparentOit" }
//         Blend 0 One One                 // accum: additive weighted sum
//         Blend 1 Zero OneMinusSrcColor   // revealage: running (1 - a) product
//         ZWrite Off                      // tested vs opaque depth, never written
//         ZTest LEqual
//         ...
//         #include "Includes/OIT.hlsl"
//         // vertex: pass a positive view-space depth interpolant
//         //   o.viewDepth = -TransformWorldToView(positionWS).z;
//         // fragment: return OutputOit(shadedColor, alpha, input.viewDepth);
//     }
//
// IMPORTANT: this is for OVER (alpha) blending only. ADDITIVE VFX (fire, sparks, glows) are
// already order-independent — render them in a plain `Blend One One` pass straight into the
// color buffer, NOT through this weighted average (it would dim them).

// MRT layout the accumulation pass writes. Target 0 = weighted premultiplied color + weight
// (RGBA16F); target 1 = the fragment's alpha, multiplied into the revealage buffer (R8).
struct OitFragOutput
{
    float4 accum     : SV_Target0; // sum( float4(rgb * a, a) * w )
    float  revealage : SV_Target1; // a, folded into prod(1 - a) by the Blend Zero OneMinusSrcColor
};

// Depth weight: nearer fragments dominate via a 4th-power falloff over a characteristic
// distance. The 50.0 is the scene-scale knob (~the top-down play area in world units) — if a
// very different world scale or camera range makes transparency read too flat (no depth
// ordering) or too near-dominant, this is the value to tune (or promote to a global). Clamped
// to keep the fp16 accum away from overflow / underflow.
float OitWeight(float alpha, float viewDepth)
{
    return clamp(0.03 / (1e-5 + pow(viewDepth / 50.0, 4.0)), 1e-2, 3e3);
}

// Build the MRT output from a shaded (straight-alpha) color, its alpha, and the positive
// view-space depth of the fragment.
OitFragOutput OutputOit(float3 color, float alpha, float viewDepth)
{
    float w = OitWeight(alpha, viewDepth);

    OitFragOutput o;
    o.accum = float4(color * alpha, alpha) * w;
    o.revealage = alpha;
    return o;
}

#endif // TDRP_OIT_INCLUDED
