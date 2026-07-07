using System;
using UnityEngine;

namespace TDRP
{
	public enum TonemapMode
	{
		None = 0,
		Reinhard = 1,
		Aces = 2,
	}

	public enum AmbientOcclusionMethod
	{
		// Normal-oriented hemisphere sampling — simple, biased, cheap.
		Hemisphere = 0,
		// Ground-Truth Ambient Occlusion — horizon-based, cosine-correct arc integral. Higher
		// quality (sharper, more accurate contacts) for a similar tap budget.
		GroundTruth = 1,
	}

	public enum AntiAliasingMode
	{
		// No anti-aliasing — the tonemapped image is presented directly.
		None = 0,
		// SMAA 1x — morphological (post-process) edge AA. No temporal component, so no ghosting;
		// the choice for a maximum-clarity target. NOTE: the SMAA passes are wired in a later step;
		// selecting this before then is a no-op (the plumbing/LDR split exists, the filter doesn't).
		Smaa = 1,
	}

	public enum UpscaleMode
	{
		// Plain bilinear stretch (the present blit's default). Cheap, but soft/aliased below native.
		Bilinear = 0,
		// AMD FidelityFX Super Resolution 1.0 — spatial edge-adaptive upscale (EASU) + contrast-adaptive
		// sharpen (RCAS). No motion vectors/jitter/history (that's FSR2/DLSS), so it slots into the
		// existing present path. Much crisper edges than bilinear when rendering below display res.
		Fsr1 = 1,
	}

	public enum TransparencyMode
	{
		// Classic painter's algorithm: draw transparents back-to-front in one pass, blending
		// "over". Exact for non-intersecting, convex, separable surfaces; breaks (wrong order)
		// on interpenetrating or concave geometry, and pays a per-frame depth sort.
		Sorted = 0,
		// Weighted Blended OIT (McGuire-Bavoil 2013): accumulate all transparents order-
		// independently into a weighted-sum + revealage buffer, then composite. No sort, robust
		// to intersection, but an APPROXIMATION — many overlapping layers at similar depth blend
		// toward an average rather than true "over".
		WeightedBlended = 1,
	}

	[Serializable]
	public class PipelineSettings
	{
		public bool UseHdr = true;
		public TonemapMode Tonemap = TonemapMode.Aces;

		[Header("Resolution")]
		// Internal render-resolution multiplier. Every offscreen buffer (CameraColor/Depth/Normals and
		// every screen-space effect derived from them — SSAO, RC, shadows, SMAA) is allocated at
		// pixelSize * this; only the final present upscales back to the display. Since ~2/3 of the frame
		// is resolution-bound fullscreen work, this is the single highest-leverage perf/quality dial:
		// 0.8 is ~36% fewer fragment invocations across that whole half of the pipeline, and for a far
		// top-down camera it's usually imperceptible. <1 = upscale (cheaper), 1 = native, >1 = supersample
		// (sharper, dearer). The shadow map is NOT affected (it has its own resolution setting).
		[Tooltip("Internal render scale. <1 renders below display res then upscales (cheaper); 1 = native; >1 supersamples. Does not affect the shadow map.")]
		[Range(0.25f, 2f)]
		public float RenderScale = 1f;

		// How the render-res image is brought back up to the display when RenderScale < 1. Bilinear =
		// the present blit's plain stretch (soft). Fsr1 = AMD FSR 1.0 (edge-adaptive EASU upscale +
		// RCAS sharpen) — edge-aware, much crisper, at the cost of two extra display-res fullscreen
		// passes. Inert at RenderScale >= 1 (nothing to upscale; present handles the 1:1 / downscale).
		[Tooltip("Upscaler used when RenderScale < 1. Bilinear = plain stretch; FSR 1 = AMD edge-adaptive EASU + RCAS sharpen (crisper). No effect at RenderScale >= 1.")]
		public UpscaleMode Upscale = UpscaleMode.Bilinear;
		// FSR 1 RCAS sharpening, authored in "stops": 0 = maximum sharpening, higher = softer (the
		// shader applies exp2(-this)). AMD's default is ~0.2. Only used when Upscale = Fsr1.
		[Tooltip("FSR 1 sharpening amount in stops: 0 = sharpest, higher = softer. AMD default ~0.2. Only used when Upscale = Fsr1.")]
		[Range(0f, 2f)]
		public float FsrSharpness = 0.2f;

		[Header("Anti-aliasing")]
		// Post-process AA applied after tonemap, on the LDR image. None = present the tonemapped
		// image directly. SMAA = morphological edge AA (no temporal history -> no ghosting). The SMAA
		// passes themselves are added in a later step; until then this only selects the (identity)
		// present path — the tonemap/LDR split that makes SMAA insertable is what Phase 0 put in place.
		[Tooltip("Post-process anti-aliasing. SMAA passes are wired in a later step; selecting SMAA now is a no-op.")]
		public AntiAliasingMode AntiAliasing = AntiAliasingMode.None;

		[Header("Ambient")]
		// Hemisphere ambient (indirect) light: the sky color lights up-facing surfaces, the ground
		// color lights down-facing ones, blended by the world normal. This is the term SSAO
		// modulates — replaces the old flat RenderSettings.ambientLight. Colors are authored in
		// sRGB (the inspector swatch) and converted to linear at bind time.
		public Color AmbientSkyColor = new(0.42f, 0.48f, 0.56f, 1f);
		public Color AmbientGroundColor = new(0.20f, 0.18f, 0.16f, 1f);
		[Range(0f, 4f)]
		public float AmbientIntensity = 1f;

		[Header("Indirect / IBL")]
		// Master toggle for global image-based lighting: SH-L2 diffuse irradiance + split-sum specular
		// reflection, both from the scene's skybox (via RenderSettings.ambientProbe + the engine's
		// default reflection cubemap, prefiltered by us). When off, the diffuse indirect falls back to
		// the sky/ground hemisphere above and there is no environment specular. Fixes flat/dark metals.
		public bool ImageBasedLighting = false;
		// Overall multiplier on the indirect (IBL) contribution — a single dimmer over both the SH
		// diffuse and the env specular, on top of the scene's ambient intensity.
		[Range(0f, 4f)]
		public float EnvIntensity = 1f;

		[Header("Radiance Cascades (diffuse GI)")]
		// Master toggle for screen-space Radiance Cascades — dynamic diffuse GI that REPLACES the flat
		// SH/hemisphere ambient with a spatially-varying, emissive-reactive estimate (nearby lit/emissive
		// surfaces bleed their color onto neighbours). MILESTONE 1: a single cascade, full-res, no
		// hierarchy/temporal/denoise yet — expect noise near emitters and only local reach. Gathers from
		// the PREVIOUS frame's color, so it needs a frame to warm up and self-limits via albedo (< 1).
		// Only the diffuse term is affected; global specular IBL is untouched.
		[Tooltip("Screen-space Radiance Cascades diffuse GI (Milestone 1: single cascade, full-res, no denoise). Replaces the SH/hemisphere diffuse ambient near lit/emissive geometry.")]
		public bool ScreenSpaceRadianceCascades = false;
		// Rays cast per pixel, evenly spaced around the screen plane (+ a per-pixel rotation). More =
		// smoother azimuthal coverage / less noise, linearly more cost (cost = RcRayCount * RcStepCount).
		[Range(4, 32)]
		public int RcRayCount = 8;
		// March steps per ray out to RcMaxDistance. More = catches thinner/closer occluders and finer
		// reach, linearly more cost.
		[Range(4, 64)]
		public int RcStepCount = 24;
		// How far (WORLD units) each ray reaches for bounce light. Larger = broader color bleed but the
		// fixed step count spreads thinner (undersamples). This is the single cascade's reach; the cheap
		// long-range hierarchy is a later milestone.
		[Range(0.1f, 20f)]
		public float RcMaxDistance = 5f;
		// Scales the GI DEVIATION from the ambient baseline (not the baseline itself), so open areas stay
		// unchanged for any value: 0 = plain ambient (RC off-equivalent look), 1 = the gathered estimate,
		// >1 = punchier local bleed.
		[Range(0f, 4f)]
		public float RcIntensity = 1f;
		// Depth bias (world units) on the occluder test, so a surface doesn't self-occlude from depth
		// precision. Too high = bleed peels away from contacts.
		[Range(0f, 0.5f)]
		public float RcDepthBias = 0.05f;
		// Floor on the cosine weight of a hit, so grazing (near-horizontal) hits still contribute a
		// little rather than hard-zeroing. 0 = strict cosine (weak flat bleed); small values keep some.
		[Range(0f, 1f)]
		public float RcCosineFloor = 0.1f;
		// Sky-visibility floor. The 2D horizontal gather can't see the upper hemisphere, so a horizontal
		// hit over-counts sky occlusion (it blocks only that azimuth's horizon, not the sky above it).
		// This leaks a fraction of the ambient back through every hit so shadowed-but-open-above ground
		// doesn't over-darken. 0 = full 2D occlusion (shadow pockets can go quite dark); 1 = hits never
		// darken below ambient (bounce only brightens/tints). ~0.5 keeps shadows readable.
		[Tooltip("How much overhead-sky ambient leaks through a horizontal hit (the 2D gather can't see straight up). 0 = shadow pockets can darken hard; 1 = RC never darkens below ambient.")]
		[Range(0f, 1f)]
		public float RcSkyVisibility = 0.5f;
		// Spatial denoise for the RC irradiance: a separable depth/normal cross-bilateral blur. Diffuse
		// GI is low-frequency, so a wide blur removes the per-pixel ray-fan noise with almost no detail
		// loss and — unlike temporal accumulation — ZERO ghosting. On is the normal mode; off shows the
		// raw noisy gather (useful for A/B).
		public bool RcDenoise = true;
		// Blur half-width in pixels (taps per side, each axis). Larger = smoother but softer bounce
		// boundaries and more cost. GI tolerates a wide radius well.
		[Range(1, 8)]
		public int RcBlurRadius = 4;

		// Temporal accumulation for the RC irradiance: reproject last frame's result (motion vectors when
		// available, else camera-only) and blend it with this frame's. The GI probe grid is fixed in
		// SCREEN space, so as the camera/objects move a world point crosses probe cells and samples
		// different quantized radiance each frame — the "crawl"/shimmer that shrinking RcProbeSpacing only
		// dilutes. Averaging reprojected frames smooths that frame-to-frame delta. Disocclusion reject +
		// a neighborhood clamp bound the ghosting; the trade is a little lag on fast lighting change,
		// which low-frequency GI tolerates well. (Reconstructing a DENSER grid — not just steadier —
		// additionally needs per-frame probe/ray jitter, a later step.)
		public bool RcTemporal = true;
		// Blend weight of the CURRENT frame each accumulated frame. Lower = more history = smoother /
		// less shimmer but more lag & ghost-proneness; higher = snappier but noisier. ~0.1 keeps ~10
		// frames of history. Mirrors SsaoTemporalAlpha.
		[Range(0.02f, 1f)]
		public float RcTemporalAlpha = 0.1f;
		// Adaptive snap: where the current irradiance still disagrees with the clamped history (a moving
		// bounce the motion vectors can't capture), bias the blend toward current in proportion to the
		// disagreement, so lighting changes resolve fast instead of trailing. 0 = pure RcTemporalAlpha
		// everywhere (max smoothing, most ghosting). Mirrors SsaoTemporalResponse.
		[Range(0f, 1f)]
		public float RcTemporalResponse = 0.5f;
		// Probe-grid jitter strength (needs RcTemporal). Per frame the probe grid is offset a fraction of
		// a cell so temporal accumulation reconstructs a denser effective grid — dissolving the fixed-grid
		// crawl that survives at large RcProbeSpacing. Auto-scaled by camera motion (≈0 at rest so a static
		// view doesn't "breathe", full while panning where the crawl actually shows), so this is the ceiling
		// applied during motion: 1 = full-cell (max crawl fix, but heaviest during fast pans), lower =
		// gentler. 0 = jitter off. Pair with a low RcTemporalResponse for the strongest crawl smoothing.
		[Range(0f, 1f)]
		public float RcJitterScale = 1f;

		// Use the full Radiance Cascades HIERARCHY (probe-atlas cascades, merged near->far) instead of
		// the single-cascade M1 gather. This is the real technique: near field gets dense-spatial/few-
		// angular probes, far field sparse-spatial/many-angular, merged by ray continuation — so GI
		// reaches far at ~constant per-cascade cost. Off = the M1 single gather (RcMaxDistance-capped).
		[Tooltip("Full Radiance Cascades hierarchy (multi-scale probes, merged near->far) vs the single-cascade M1 gather. The hierarchy reaches much farther at log cost.")]
		public bool RcUseCascades = false;
		// Number of cascades. Each adds a 4x-longer interval, so reach grows ~4^N: more = far-longer GI
		// range for a near-constant per-cascade cost (memory grows linearly). 1 ~= the single gather.
		[Range(1, 6)]
		public int RcCascadeCount = 4;
		// Cascade-0 probe spacing in pixels (the finest spatial resolution). Larger = fewer probes /
		// cheaper + smaller cascade textures, but blockier near-field GI (the resolve + blur hide some).
		[Range(1, 8)]
		public int RcProbeSpacing = 2;
		// World length of cascade 0's ray interval (the near-field annulus [0, this]). Each higher
		// cascade's interval is 4x the previous, so total reach = this * (4^N - 1)/3. Larger = coarser
		// near field but longer total reach.
		[Range(0.1f, 4f)]
		public float RcCascadeInterval0 = 0.5f;
		// Debug/teaching toggle: when off, cascades are built INDEPENDENTLY (no near->far merge), so only
		// cascade 0's near interval contributes — useful to isolate the near field from the merged long
		// range. On (normal) merges the hierarchy so far bounce reaches the near probes.
		public bool RcCascadeMerge = true;
		// Base ray directions at cascade 0 (must be a perfect square: 4 or 16). This is the biggest lever
		// on the two RC artifacts: with too few directions each covers a wide angular wedge, so a bright
		// object is only "seen" along discrete rays (SPOKES radiating from it) and fills its whole wedge
		// regardless of distance (HALOS). 16 gives much finer wedges (fewer spokes, natural distance
		// falloff) at ~4x the cascade-texture cost — pair with a larger RcProbeSpacing to keep memory flat.
		[Tooltip("Cascade-0 directions (4 or 16). 16 = far fewer spokes/halos but 4x cascade-texture memory; raise RcProbeSpacing to compensate.")]
		public int RcBaseDirections = 4;
		// Distance (world units) over which a hit's bounce falls off (inverse-square-ish). Cheap fix for
		// the residual halo that coarse angular sampling leaves — far bright objects contribute less.
		// 0 = no falloff (physically-pure but haloes with few directions).
		[Range(0f, 40f)]
		public float RcFalloffDistance = 10f;
		// Firefly clamp on the bounce radiance (world units of luminance, Rec.709; hue-preserving). A
		// single very bright prev-frame texel — an emissive face, a specular highlight — is a spike of
		// incoming radiance that the coarse ray fan hits inconsistently as the camera/objects move, so it
		// flickers as the "specks" the eye catches on motion. Capping luminance before the spike enters the
		// merge removes those outliers without dimming ordinary diffuse bounce (which sits well below the
		// cap). Set just above the brightest surface you want to bounce; 0 = disabled (physically pure).
		[Range(0f, 20f)]
		public float RcFireflyClamp = 5f;
		// Run the whole RC pipeline (cascade build + resolve + blur) at half resolution, then a depth-
		// aware bilateral upsample to full res. GI is low-frequency so this is ~free quality-wise at ~4x
		// less cost — the same trade as SSAO's half-res. Off = full-res RC. Applies to both the cascade
		// and the M1-gather paths.
		public bool RcHalfResolution = true;

		// Debug: overlay a per-tile heatmap of the forward+ cluster light counts.
		public bool ShowClusterHeatmap = false;
		// Debug: replace the view with the shared world-space normal buffer (n*0.5+0.5),
		// to inspect the depth+normal prepass output. Takes precedence over ShowClusterHeatmap.
		public bool ShowNormals = false;
		// Debug: replace the view with the raw SSAO buffer (grayscale). Takes precedence over
		// the other debug views. Requires Ssao (or this) to be on so the buffer is produced.
		public bool ShowAO = false;
		// Debug: replace the view with the raw Radiance Cascades irradiance buffer (HDR color: bounce
		// halos around lit/emissive geometry, flat ambient in the open). Requires
		// ScreenSpaceRadianceCascades on AND a warmed-up color history so the buffer is produced.
		public bool ShowRcRadiance = false;
		// Debug: replace the view with the screen-space motion vector buffer, encoded as color
		// (direction -> hue, magnitude -> brightness). Requires MotionVectors (or this) to be on
		// so the buffer is produced.
		public bool ShowMotionVectors = false;
		// Debug: replace the view with the SMAA edge mask (R = horizontal edges, G = vertical edges;
		// black = no edge). Detected on the tonemapped LDR image, so it's shown post-tonemap (a raw
		// copy, not re-tonemapped). Independent of the AntiAliasing setting — it only produces + shows
		// the edge buffer, it does not blend. Highest precedence among the debug views.
		public bool ShowSmaaEdges = false;
		// Debug: replace the view with the environment-BRDF LUT (R = F0 scale, G = bias) so the
		// split-sum integration can be inspected in isolation. Independent of ImageBasedLighting (the
		// LUT is always baked). Takes precedence over the other debug views.
		public bool ShowBrdfLut = false;
		// Debug: replace the view with the GGX-prefiltered environment cubemap, sampled by view
		// direction at a chosen roughness (see PrefilteredEnvDebug). Requires ImageBasedLighting so the
		// cube is baked.
		public bool ShowPrefilteredEnv = false;
		// Roughness level (0 = mirror, 1 = fully rough) used by the ShowPrefilteredEnv view to pick the
		// mip, so the blurring-per-roughness can be inspected.
		[Range(0f, 1f)]
		public float ShowPrefilteredEnvRoughness = 0f;
		// Debug: re-run the IBL bakes (env-BRDF LUT + GGX prefilter) EVERY frame while checked, ignoring
		// the normal once-baked / source-unchanged gates. For iterating on the bake shaders or the cube's
		// RT allocation without a domain reload — check, observe, uncheck. Wasteful to leave on.
		[Tooltip("Debug: force the IBL bakes to re-run every frame (ignores the baked-state gates). Leave off in normal use.")]
		public bool ForceIblRebake = false;

		[Header("Motion Vectors")]
		// Master toggle: produce the per-pixel screen-space velocity buffer (_CameraMotionVectorsTexture).
		// A fullscreen camera-motion pass (from depth) fills it, then moving objects overwrite their
		// pixels via their MotionVectors shader pass. The prerequisite for TAA / motion blur, and the
		// input a later step will feed into the temporal AO / shadow reprojection (replacing the
		// current camera-only reprojection that ghosts on moving geometry).
		public bool MotionVectors = false;

		[Header("SSAO")]
		// Master toggle for screen-space ambient occlusion (modulates the ambient term).
		public bool Ssao = false;
		// Which estimator produces the raw AO. Hemisphere = the simple original; GroundTruth =
		// GTAO. The rest of the chain (blur/temporal/upsample/composite) is identical for both.
		// NOTE: the two estimators have DIFFERENT quality knobs — Hemisphere uses SsaoSampleCount;
		// GTAO uses GtaoSliceCount x GtaoStepsPerSlice. Each ignores the other's.
		[Tooltip("Hemisphere quality = SsaoSampleCount. GTAO quality = GtaoSliceCount x GtaoStepsPerSlice. Each estimator ignores the other's knob.")]
		public AmbientOcclusionMethod AoMethod = AmbientOcclusionMethod.Hemisphere;
		// GTAO only: number of slices (screen-space directions). More = smoother azimuthal
		// coverage, linearly more cost. Total taps ≈ GtaoSliceCount * GtaoStepsPerSlice * 2.
		[Tooltip("GTAO ONLY (no effect under Hemisphere). Screen-space slice directions; more = smoother azimuthal coverage, linearly more cost.")]
		[Range(1, 8)]
		public int GtaoSliceCount = 3;
		// GTAO only: horizon march steps per slice direction. More = catches finer/closer
		// occluders, linearly more cost.
		[Tooltip("GTAO ONLY (no effect under Hemisphere). Horizon march steps per slice; more = catches finer/closer occluders, linearly more cost.")]
		[Range(1, 8)]
		public int GtaoStepsPerSlice = 4;
		// Hemisphere sample radius in WORLD units — how far AO "reaches". Larger = broader, softer
		// contact darkening (good for top-down), but more prone to haloing and undersampling.
		[Range(0.05f, 5f)]
		public float SsaoRadius = 0.5f;
		// Strength of the darkening. 1 = the raw occlusion estimate; >1 deepens it (stylised).
		[Range(0f, 4f)]
		public float SsaoIntensity = 1.5f;
		// Depth bias (world units) added before the occlusion compare, to stop a surface
		// shadowing itself due to depth/normal precision. Too high = AO peels away from contacts.
		[Range(0f, 0.5f)]
		public float SsaoBias = 0.025f;
		// Samples per pixel — HEMISPHERE ESTIMATOR ONLY. GTAO ignores this entirely (it's a
		// different algorithm — horizon marching, set by GtaoSliceCount x GtaoStepsPerSlice). More =
		// smoother raw AO but linearly more cost; denoise/temporal let it drop hard (as few-tap PCSS).
		[Tooltip("Hemisphere ONLY. GTAO ignores this — use GtaoSliceCount x GtaoStepsPerSlice for GTAO quality.")]
		[Range(4, 32)]
		public int SsaoSampleCount = 16;
		// Compute AO (estimate + blur + temporal) at half resolution, then joint-bilateral upsample
		// to full res. ~4x fewer AO pixels; the far top-down camera hides the quality cost. Off =
		// full-res AO.
		public bool SsaoHalfResolution = true;
		// Edge-aware (depth/normal) bilateral blur to resolve the raw AO noise. Separable, two
		// passes. Off = view the raw estimate (useful for A/B). On is the normal mode.
		public bool SsaoDenoise = true;
		// Temporally accumulate the AO: jitter the sample rotation per frame and blend with a
		// reprojected history, so a low SsaoSampleCount converges to a clean result (as with the
		// few-tap PCSS). Cost is temporal lag / ghosting on motion — off by default, worth evaluating.
		public bool SsaoTemporal = false;
		// Fraction of the CURRENT frame blended in each frame. Lower = smoother / less noise but
		// more lag; higher = noisier but more responsive. ~0.1 ≈ a 10-frame average.
		[Range(0.02f, 1f)]
		public float SsaoTemporalAlpha = 0.1f;
		// Adaptive temporal response: where this frame's AO DISAGREES with the accumulated history
		// even after the neighborhood clamp — a moving occluder sweeping across a static surface,
		// which the receiver's motion vector can't capture — bias the blend toward the current frame
		// in proportion to that disagreement. 0 = pure SsaoTemporalAlpha everywhere (max smoothing,
		// max lag/ghosting on movers); higher = snaps to the new value in changing regions (kills the
		// trailing smear, at a little transient noise there while it converges).
		[Range(0f, 1f)]
		public float SsaoTemporalResponse = 0.5f;

		[Header("Light Cookie")]
		// Master toggle: project a tiling texture straight down over the world to modulate the main
		// directional light's color (drifting cloud shadows / dappled canopy — very RTS). Inert
		// unless a cookie texture is also assigned on the pipeline asset.
		public bool MainLightCookie = false;
		// World size (in units) of one cookie tile. Larger = the pattern is stretched over more
		// ground (bigger, sparser clouds); smaller = it repeats more tightly.
		[Min(0.01f)]
		public float CookieTiling = 20f;
		// Drift speed of the cookie across the ground, in world units/second (X and Z). Non-zero =
		// clouds visibly scroll. Driven by Time.time, so the motion is only continuous in play mode.
		public Vector2 CookieScrollSpeed = Vector2.zero;
		// Blend between no cookie (0 = light unchanged) and the full sampled cookie (1). A dimmer for
		// dialing the effect in without editing the texture.
		[Range(0f, 1f)]
		public float CookieStrength = 1f;

		[Header("Transparency")]
		// How transparent (Queue=Transparent) geometry is composited. Sorted = the classic
		// back-to-front pass (default, exact for well-behaved geometry). WeightedBlended = the
		// order-independent approximation (no sort, robust to intersection). Both drive the same
		// materials — the WBOIT path just invokes the material's TDRPTransparentOit pass instead.
		public TransparencyMode TransparencyMode = TransparencyMode.Sorted;

		[Header("Shadows")]
		// Master toggle for the main directional light's shadow map.
		public bool MainLightShadows = true;
		// Shadow map resolution (square). Larger = sharper but more memory + fill.
		public int ShadowResolution = 2048;
		// World-space distance from the camera the directional shadow covers. The shadow
		// is fit to where the view frustum hits the ground plane within this range, so
		// smaller = higher effective resolution (texels cover less world space).
		public float ShadowDistance = 50f;
		// The shadow fit targets this horizontal ground plane (Y = height). For a top-down
		// game the play area is the ground, so fitting to it wastes no resolution on the
		// empty air the camera frustum also spans.
		public float ShadowPlaneHeight = 0f;
		// Vertical band above/below the plane included in the shadow volume, so objects
		// standing on (or sunk into) the ground still cast. Raise Above for tall objects.
		public float ShadowHeightAbove = 20f;
		public float ShadowHeightBelow = 5f;
		// Fit the shadow map as a tight rectangle to the footprint instead of a square.
		// Sharper on the shallow axis (no wasted resolution); texels become anisotropic and
		// the fit can shimmer under camera rotation. Untick for a rotation-stable square fit.
		public bool RectangularShadowFit = true;
		// Depth + normal bias are read from the Light component's own Bias / Normal Bias
		// sliders (the standard per-light place to tune them), scaled by texel size.

		// PCSS (Percentage-Closer Soft Shadows): variable-width penumbra with contact
		// hardening, instead of the constant-softness 3x3 PCF. Only takes effect when the
		// main light's Shadow Type is Soft; otherwise the hard single-tap path is used.
		public bool UsePcss = false;
		// Angular diameter of the sun disk, in degrees. This is what scales the penumbra for
		// a DIRECTIONAL light (there's no point-source radius): a wider sun => wider penumbra.
		// The real sun is ~0.53°; raise it well past physical for a stylised soft look.
		[Range(0.1f, 5f)]
		public float SunAngularDiameter = 0.53f;
		// PCSS sample taps per frame (blocker search + filter). Fewer = cheaper but noisier per
		// frame; temporal accumulation reconstructs the quality, so with TemporalShadows on you can
		// drop this for a big perf win (e.g. 4-8 taps converge to ~16-tap quality over a few frames).
		// Applies to the screen-space path; transparents always use the full 16.
		[Range(2, 16)]
		public int PcssTapCount = 8;
		// Accelerate the PCSS blocker search with a coarse min/max-depth pyramid: whole search
		// windows that are provably all-lit or all-shadowed skip the per-pixel search. Lossless
		// (the image is identical with it on or off) — toggle off only to A/B the cost.
		public bool UseShadowBlockerPyramid = true;

		// Resolve the main light's shadow into a full-screen mask (a separate pass) instead of
		// sampling it inline per lit pixel. On its own it should look identical to inline
		// shadows; it's the prerequisite for temporal accumulation. Transparents always use the
		// inline path (they aren't in the depth buffer the mask is built from).
		public bool ScreenSpaceShadows = false;

		// Temporally accumulate the screen-space shadow mask: blend each frame's noisy PCSS result
		// with a reprojected history buffer, so the noise averages out for ~free. Requires
		// ScreenSpaceShadows. Cost is temporal lag / ghosting on moving shadows (the reason this is
		// off by default and worth evaluating rather than assuming).
		public bool TemporalShadows = false;
		// Fraction of the CURRENT frame blended in each frame. Lower = smoother / less noise but
		// more lag and ghosting; higher = noisier but more responsive. ~0.1 ≈ a 10-frame average.
		[Range(0.02f, 1f)]
		public float TemporalShadowAlpha = 0.1f;
		// Adaptive temporal response for the shadow mask (the shadow counterpart to
		// SsaoTemporalResponse): where history falls OUTSIDE the current neighborhood range — a stale
		// ghost trail from a moving shadow — bias the blend toward the current frame in proportion to
		// how far it's off. Driven by the clamp correction (not raw-vs-history), so per-frame PCSS
		// noise does NOT trigger it and the penumbra keeps its full denoising. 0 = pure
		// TemporalShadowAlpha everywhere (max smoothing, max ghost lag); higher = snaps the trail out.
		[Range(0f, 1f)]
		public float TemporalShadowResponse = 0.5f;

		// Spatial denoise for the screen-space shadow mask: a variance-modulated depth/normal
		// bilateral blur. The non-temporal way to clean up few-tap PCSS dither — it smooths across
		// SPACE instead of across frames, so there's no motion ghosting, at the cost of a little
		// penumbra softening. Use it instead of (or alongside) TemporalShadows. Requires
		// ScreenSpaceShadows (it filters the mask); runs after the temporal resolve when both are on.
		public bool ShadowDenoise = false;
		// Blur half-width in pixels (taps per side, each axis). Larger = smoother but softer penumbra.
		[Range(1, 8)]
		public int ShadowBlurRadius = 3;
		// How aggressively local variance drives the blur. The blur only fires where the mask actually
		// varies (the noisy penumbra), leaving flat fully-lit / fully-shadowed regions crisp. Higher =
		// smooths at lower variance (approaches a plain always-on bilateral); lower = only the noisiest
		// regions; 0 effectively disables it (nothing passes the variance gate).
		[Range(0f, 40f)]
		public float ShadowBlurVarianceSensitivity = 16f;
	}
}
