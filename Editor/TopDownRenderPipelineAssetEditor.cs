using UnityEditor;
using UnityEngine;

namespace TDRP.EditorTools
{
	// Custom inspector for the pipeline asset. It replaces the default flat property list with
	// feature-grouped foldouts, per-field tooltips, dependency-aware greying (a knob greys out when
	// the feature that consumes it is off), and a performance note per group stating how cost scales.
	//
	// Convention used below:
	//   - Each performance-relevant group opens with a HelpBox giving the dominant O(n) cost law.
	//   - Individual heavy knobs start their tooltip with "PERF:" so hovering reveals the cost.
	//   - "pixels" below means screen pixels at full res; a half-resolution toggle divides that by 4.
	[CustomEditor(typeof(TopDownRenderPipelineAsset))]
	[CanEditMultipleObjects]
	public class TopDownRenderPipelineAssetEditor : Editor
	{
		private SerializedProperty _settings;

		// Foldout open/closed state, persisted across domain reloads via EditorPrefs.
		private const string PrefPrefix = "TDRP.Inspector.Foldout.";

		private void OnEnable()
		{
			_settings = serializedObject.FindProperty("_settings");
		}

		public override void OnInspectorGUI()
		{
			serializedObject.Update();

			EditorGUILayout.HelpBox(
				"Grouped view of the pipeline settings. Groups tagged with a performance note list how their " +
				"GPU cost scales; individual heavy knobs prefix their tooltip with \"PERF:\". Knobs grey out " +
				"when the feature that reads them is disabled.",
				MessageType.None);

			DrawOutput();
			DrawAmbientIndirect();
			DrawRadianceCascades();
			DrawShadows();
			DrawSsao();
			DrawTransparency();
			DrawLightCookie();
			DrawMotionVectors();
			DrawDebugViews();
			DrawReferences();

			serializedObject.ApplyModifiedProperties();
		}

		// ---------------------------------------------------------------------------------------------
		// Groups
		// ---------------------------------------------------------------------------------------------

		private void DrawOutput()
		{
			if (!BeginGroup("output", "Output & Color")) { EndGroup(); return; }

			Perf(
				"RenderScale multiplies EVERY offscreen buffer's resolution — the dominant perf lever, since most of\n" +
				"the frame is fullscreen O(pixels) work (RC, SSAO, shadows, SMAA). 0.8 ~= 0.64x the fragment work.\n" +
				"HDR widens the color target (B10G11R11 vs R8G8B8A8) — ~1.4x color bandwidth, no extra ALU.\n" +
				"Tonemap + SMAA are fullscreen passes: O(pixels), fixed work per pixel (SMAA = 3 passes).\n" +
				"FSR 1 upscaling (Upscale = Fsr1, active only when RenderScale < 1) adds 2 display-res passes (EASU + RCAS).");

			S("RenderScale", "PERF: internal render-resolution multiplier. Every offscreen buffer + screen-space effect " +
			                 "renders at pixelSize x this; only the final Present upscales to the display. <1 = cheaper " +
			                 "(upscaled), 1 = native, >1 = supersample. Highest-leverage perf/quality dial. The shadow " +
			                 "map is NOT affected (it has its own resolution). Below 1, SMAA runs at render res so edges soften a little.");

			bool fsr = E("Upscale") == 1; // 0 = Bilinear, 1 = Fsr1
			S("Upscale", "How the render-res image is upscaled to the display when RenderScale < 1. Bilinear = plain " +
			             "stretch (soft); FSR 1 = AMD edge-adaptive EASU upscale + RCAS sharpen (much crisper edges). " +
			             "Runs AFTER SMAA (a spatial upscaler needs a clean anti-aliased input). Inert at RenderScale >= 1. " +
			             "PERF: FSR adds 2 display-res fullscreen passes.");
			using (new EditorGUI.DisabledScope(!fsr))
				S("FsrSharpness", "FSR 1 RCAS sharpening, in stops: 0 = sharpest, higher = softer (shader applies exp2(-this)). " +
				                  "AMD default ~0.2. Only used when Upscale = Fsr1.");

			S("UseHdr", "Render CameraColor in a floating-point HDR format so highlights survive until tonemap. " +
			            "PERF: ~1.4x color-buffer bandwidth vs LDR; no per-pixel ALU cost.");
			S("Tonemap", "HDR->LDR curve applied in the present pass. ACES is filmic; Reinhard is cheaper/flatter; " +
			             "None clips. PERF: one fullscreen pass regardless of choice.");
			S("AntiAliasing", "Post-tonemap edge AA. SMAA is morphological (no temporal history -> no ghosting). " +
			                  "PERF: SMAA adds 3 fullscreen passes, O(pixels).");

			EndGroup();
		}

		private void DrawAmbientIndirect()
		{
			if (!BeginGroup("ambient", "Ambient & Indirect (IBL)")) { EndGroup(); return; }

			Perf("Ambient hemisphere is evaluated in-shader per lit pixel (negligible). IBL adds two precompute-ONCE " +
			     "bakes (env-BRDF LUT + GGX prefilter of the skybox cube); per-frame cost is a few texture samples.");

			EditorGUILayout.LabelField("Hemisphere ambient", EditorStyles.boldLabel);
			S("AmbientSkyColor", "Indirect light hitting up-facing surfaces (authored sRGB, linearised at bind).");
			S("AmbientGroundColor", "Indirect light hitting down-facing surfaces.");
			S("AmbientIntensity", "Master multiplier over the hemisphere ambient. This is the term SSAO modulates.");

			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("Image-based lighting", EditorStyles.boldLabel);
			S("ImageBasedLighting", "SH-L2 diffuse irradiance + split-sum environment specular from the skybox. " +
			                        "Fixes flat/dark metals. PERF: bakes run once (or on source change), not per frame.");
			using (new EditorGUI.DisabledScope(!B("ImageBasedLighting")))
				S("EnvIntensity", "Single dimmer over both the SH diffuse and the environment specular contribution.");

			EndGroup();
		}

		private void DrawRadianceCascades()
		{
			if (!BeginGroup("rc", "Radiance Cascades (diffuse GI)")) { EndGroup(); return; }

			Perf(
				"Screen-space GI, the single most tunable cost in the pipeline.\n" +
				"  M1 single gather : O(pixels x RcRayCount x RcStepCount).\n" +
				"  Cascade hierarchy: probes x directions per cascade; RcBaseDirections 4->16 is ~4x texture+resolve;\n" +
				"                     RcCascadeCount is linear memory for ~4^N reach; RcProbeSpacing divides probe count by spacing^2.\n" +
				"  Denoise blur     : O(pixels x RcBlurRadius), separable.\n" +
				"  Half Resolution divides all pixel terms by 4 (+ one upsample pass).");

			bool rc = B("ScreenSpaceRadianceCascades");
			S("ScreenSpaceRadianceCascades", "Master toggle. Replaces the flat ambient diffuse with a screen-space " +
			                                 "bounce estimate near lit/emissive geometry. Needs a warmed-up color history.");

			using (new EditorGUI.DisabledScope(!rc))
			{
				bool cascades = B("RcUseCascades");

				EditorGUILayout.LabelField("Common", EditorStyles.boldLabel);
				S("RcIntensity", "Scales the GI deviation from the ambient baseline (open areas stay unchanged at any value).");
				S("RcDepthBias", "Occluder-test depth bias so a surface doesn't self-occlude. Too high peels bleed off contacts.");
				S("RcCosineFloor", "Floor on a hit's cosine weight so grazing hits still contribute a little.");
				S("RcSkyVisibility", "How much overhead-sky ambient leaks through a horizontal hit (the 2D gather can't see " +
				                     "straight up). 0 = shadow pockets can go dark; 1 = RC never darkens below ambient.");
				S("RcHalfResolution", "PERF: run the whole RC chain at half res then bilateral-upsample. ~4x cheaper; GI is " +
				                      "low-frequency so quality cost is small.");
				S("RcDenoise", "Separable depth/normal cross-bilateral blur over the raw gather. Zero ghosting (unlike temporal).");
				using (new EditorGUI.DisabledScope(!B("RcDenoise")))
					S("RcBlurRadius", "PERF: blur half-width in pixels per axis. Cost is O(pixels x radius) per axis.");

				S("RcTemporal", "Reproject + accumulate last frame's irradiance to smooth the screen-space probe-grid " +
				                "crawl on motion. Disocclusion-reject + neighborhood-clamp guard ghosting; costs one pass " +
				                "+ a per-camera RGBA16F history. Enable MotionVectors for correct reprojection on movers.");
				using (new EditorGUI.DisabledScope(!B("RcTemporal")))
				{
					S("RcTemporalAlpha", "Current-frame blend weight. Lower = more history = smoother/less shimmer but more " +
					                     "lag; ~0.1 keeps ~10 frames.");
					S("RcTemporalResponse", "Adaptive snap where current disagrees with clamped history (fast lighting change). " +
					                        "0 = pure alpha everywhere (max smoothing).");
					S("RcJitterScale", "Probe-grid jitter ceiling (auto-scaled by camera motion: ~0 at rest so it doesn't " +
					                   "breathe, full while panning). Densifies the effective grid to kill the fixed-grid crawl. " +
					                   "1 = full-cell, 0 = off. Lower RcTemporalResponse for max effect.");
				}

				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField("Single-cascade (M1) gather", EditorStyles.boldLabel);
				using (new EditorGUI.DisabledScope(cascades))
				{
					S("RcRayCount", "PERF: rays per pixel. Cost = RcRayCount x RcStepCount. More = less azimuthal noise.");
					S("RcStepCount", "PERF: march steps per ray. Cost = RcRayCount x RcStepCount. More = catches thinner occluders.");
					S("RcMaxDistance", "World reach of the single cascade. Larger reach undersamples for a fixed step count.");
				}

				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField("Cascade hierarchy", EditorStyles.boldLabel);
				S("RcUseCascades", "Use the full multi-scale probe hierarchy (near->far merge) instead of the M1 gather. " +
				                   "Reaches much farther at ~log cost.");
				using (new EditorGUI.DisabledScope(!cascades))
				{
					S("RcCascadeCount", "PERF: each cascade adds a 4x-longer interval (reach ~4^N) for near-constant per-cascade " +
					                    "cost; memory grows linearly.");
					S("RcProbeSpacing", "PERF: cascade-0 probe spacing in pixels. Probe count (and cost) scales as 1/spacing^2.");
					S("RcCascadeInterval0", "World length of cascade 0's ray interval; each higher cascade is 4x this.");
					S("RcBaseDirections", "PERF: cascade-0 directions (4 or 16). 16 = far fewer spokes/halos but ~4x cascade-texture " +
					                      "memory and resolve cost; pair with a larger RcProbeSpacing to compensate.");
					S("RcFalloffDistance", "Distance over which a hit's bounce falls off. Cheap fix for residual halos from coarse " +
					                       "angular sampling. 0 = no falloff.");
					S("RcFireflyClamp", "Caps bounce-radiance luminance (hue-preserving). Kills the flickering 'specks' from a " +
					                    "single bright prev-frame texel the ray fan samples inconsistently on motion. 0 = disabled.");
					S("RcCascadeMerge", "Debug: off builds cascades independently (only near interval contributes). On (normal) merges.");
				}
			}

			EndGroup();
		}

		private void DrawShadows()
		{
			if (!BeginGroup("shadows", "Shadows (directional)")) { EndGroup(); return; }

			Perf(
				"Shadow map render : O(shadow-casters) draws x fill; ShadowResolution is O(n^2) memory+fill (2048->4096 = 4x).\n" +
				"PCSS screen-space : O(pixels x PcssTapCount) (blocker search + filter); blocker pyramid skips uniform tiles (lossless).\n" +
				"Screen-space mask : one fullscreen pass; Temporal adds a reproject/blend pass + a history buffer.\n" +
				"Denoise blur      : O(pixels x ShadowBlurRadius), separable, variance-gated.");

			bool shadows = B("MainLightShadows");
			S("MainLightShadows", "Master toggle for the main directional light's shadow map.");

			using (new EditorGUI.DisabledScope(!shadows))
			{
				EditorGUILayout.LabelField("Map fit", EditorStyles.boldLabel);
				S("ShadowResolution", "PERF: square shadow-map size. O(n^2) memory and fill — 2048->4096 quadruples both.");
				S("ShadowDistance", "World distance the shadow covers. Smaller = higher effective resolution (texels cover less ground).");
				S("ShadowPlaneHeight", "Ground plane (Y) the fit targets — for top-down, the play area, so no resolution is wasted on air.");
				S("ShadowHeightAbove", "Band above the plane included in the shadow volume. Raise for tall casters.");
				S("ShadowHeightBelow", "Band below the plane included in the shadow volume.");
				S("RectangularShadowFit", "Fit a tight rectangle (sharper shallow axis) vs a rotation-stable square. Rect can shimmer under rotation.");

				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField("Soft shadows (PCSS)", EditorStyles.boldLabel);
				bool pcss = B("UsePcss");
				S("UsePcss", "Variable-width penumbra + contact hardening instead of constant 3x3 PCF. Only active when the light's Shadow Type is Soft.");
				using (new EditorGUI.DisabledScope(!pcss))
				{
					S("SunAngularDiameter", "Sun disk angular size (deg); scales the penumbra for a directional light. Real sun ~0.53.");
					S("PcssTapCount", "PERF: taps/frame for blocker search + filter. Cost is O(pixels x taps). Temporal lets you drop this hard.");
					S("UseShadowBlockerPyramid", "PERF: min/max depth pyramid skips provably all-lit/all-shadowed search windows. Lossless — toggle only to A/B cost.");
				}

				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField("Screen-space & temporal", EditorStyles.boldLabel);
				bool ssShadows = B("ScreenSpaceShadows");
				S("ScreenSpaceShadows", "Resolve the shadow into a fullscreen mask (prerequisite for temporal). Should look identical to inline on its own.");
				using (new EditorGUI.DisabledScope(!ssShadows))
				{
					bool temporal = B("TemporalShadows");
					S("TemporalShadows", "PERF: accumulate the noisy PCSS mask over frames (adds a reproject pass + history buffer). Cost is temporal lag/ghosting.");
					using (new EditorGUI.DisabledScope(!temporal))
					{
						S("TemporalShadowAlpha", "Fraction of the current frame blended each frame. Lower = smoother but more lag. ~0.1 ~= 10-frame average.");
						S("TemporalShadowResponse", "Adaptive snap where history falls outside the current neighborhood (kills ghost trails). 0 = pure alpha.");
					}

					bool denoise = B("ShadowDenoise");
					S("ShadowDenoise", "Variance-gated depth/normal bilateral blur — the non-temporal way to clean few-tap PCSS (no ghosting, softer penumbra).");
					using (new EditorGUI.DisabledScope(!denoise))
					{
						S("ShadowBlurRadius", "PERF: blur half-width in pixels per axis. Cost is O(pixels x radius) per axis.");
						S("ShadowBlurVarianceSensitivity", "How readily local variance fires the blur. Higher = blurs at lower variance; 0 disables it.");
					}
				}
			}

			EndGroup();
		}

		private void DrawSsao()
		{
			if (!BeginGroup("ssao", "SSAO")) { EndGroup(); return; }

			Perf(
				"Hemisphere : O(pixels x SsaoSampleCount).\n" +
				"GTAO       : O(pixels x GtaoSliceCount x GtaoStepsPerSlice).\n" +
				"Half Resolution divides the pixel term by 4 (+ one upsample pass); Temporal adds a reproject pass + history buffer.");

			bool ssao = B("Ssao");
			S("Ssao", "Master toggle for screen-space ambient occlusion (modulates the ambient term).");

			using (new EditorGUI.DisabledScope(!ssao))
			{
				bool gtao = E("AoMethod") == 1; // 0 = Hemisphere, 1 = GroundTruth
				S("AoMethod", "Hemisphere (simple, biased) vs GTAO (horizon-based, cosine-correct). Each estimator has its OWN quality knobs.");

				using (new EditorGUI.DisabledScope(!gtao))
				{
					S("GtaoSliceCount", "PERF: GTAO only. Screen-space slice directions. Total taps ~= slices x steps x 2.");
					S("GtaoStepsPerSlice", "PERF: GTAO only. Horizon march steps per slice; more = finer/closer occluders.");
				}
				using (new EditorGUI.DisabledScope(gtao))
					S("SsaoSampleCount", "PERF: Hemisphere only. Samples/pixel; cost is linear. Denoise/temporal let it drop hard.");

				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField("Shared", EditorStyles.boldLabel);
				S("SsaoRadius", "AO reach in world units. Larger = broader/softer contact (good for top-down) but more haloing.");
				S("SsaoIntensity", "Darkening strength. 1 = raw estimate; >1 deepens (stylised).");
				S("SsaoBias", "Depth bias before the occlusion compare, to stop self-shadowing. Too high peels AO off contacts.");
				S("SsaoHalfResolution", "PERF: compute AO at half res then joint-bilateral upsample. ~4x fewer AO pixels.");
				S("SsaoDenoise", "Edge-aware bilateral blur to resolve raw AO noise (separable, two passes).");

				EditorGUILayout.Space(4);
				EditorGUILayout.LabelField("Temporal", EditorStyles.boldLabel);
				bool temporal = B("SsaoTemporal");
				S("SsaoTemporal", "PERF: jitter + reproject a history so a low sample count converges clean (adds a pass + history). Cost is lag/ghosting.");
				using (new EditorGUI.DisabledScope(!temporal))
				{
					S("SsaoTemporalAlpha", "Fraction of the current frame blended each frame. ~0.1 ~= a 10-frame average.");
					S("SsaoTemporalResponse", "Adaptive snap where this frame disagrees with history (kills the smear on movers). 0 = pure alpha.");
				}
			}

			EndGroup();
		}

		private void DrawTransparency()
		{
			if (!BeginGroup("transparency", "Transparency")) { EndGroup(); return; }

			Perf("Sorted     : per-frame back-to-front sort O(n log n) + overdraw fill.\n" +
			     "WeightedBlended (WBOIT): no sort, 2 passes (accumulate + composite), O(overdraw); extra accum + revealage buffers.");

			S("TransparencyMode", "Sorted = classic back-to-front (exact for well-behaved geometry). WeightedBlended = order-" +
			                      "independent approximation (no sort, robust to intersection). Both drive the same materials.");

			EndGroup();
		}

		private void DrawLightCookie()
		{
			if (!BeginGroup("cookie", "Light Cookie")) { EndGroup(); return; }

			Perf("One extra texture sample in the lighting path — negligible. Inert unless a cookie texture is assigned on the asset (References).");

			bool cookie = B("MainLightCookie");
			S("MainLightCookie", "Project a tiling texture straight down to modulate the main light (cloud shadows / dappled canopy).");
			using (new EditorGUI.DisabledScope(!cookie))
			{
				S("CookieTiling", "World size of one cookie tile. Larger = bigger, sparser pattern.");
				S("CookieScrollSpeed", "Drift speed across the ground (world units/sec, X/Z). Driven by Time.time (continuous only in play mode).");
				S("CookieStrength", "Blend between no cookie (0) and the full sampled cookie (1).");
			}

			EndGroup();
		}

		private void DrawMotionVectors()
		{
			if (!BeginGroup("motion", "Motion Vectors")) { EndGroup(); return; }

			Perf("One fullscreen camera-motion pass (from depth) + per-object motion draws for movers; adds a velocity buffer. O(pixels + mover verts).");

			S("MotionVectors", "Produce the screen-space velocity buffer. Prerequisite for TAA / motion blur and for motion-aware temporal reprojection.");

			EndGroup();
		}

		private void DrawDebugViews()
		{
			if (!BeginGroup("debug", "Debug Views", defaultOpen: false)) { EndGroup(); return; }

			EditorGUILayout.HelpBox("Authoring aids: each replaces the final image with an intermediate buffer (one cheap fullscreen pass). " +
			                        "Leave all off for normal rendering. Higher entries generally take precedence.", MessageType.None);

			S("ShowClusterHeatmap", "Overlay a per-tile heatmap of the forward+ cluster light counts.");
			S("ShowNormals", "Replace the view with the world-space normal buffer (n*0.5+0.5).");
			S("ShowAO", "Replace the view with the raw SSAO buffer. Requires SSAO (or this) on.");
			S("ShowRcRadiance", "Replace the view with the raw Radiance Cascades irradiance. Requires RC on + warmed-up history.");
			S("ShowMotionVectors", "Replace the view with the motion-vector buffer (direction->hue, magnitude->brightness). Requires Motion Vectors (or this) on.");
			S("ShowSmaaEdges", "Replace the view with the SMAA edge mask (R=horizontal, G=vertical).");
			S("ShowBrdfLut", "Replace the view with the environment-BRDF LUT (always baked; independent of IBL toggle).");
			S("ShowPrefilteredEnv", "Replace the view with the GGX-prefiltered environment cube. Requires IBL so the cube is baked.");
			S("ShowPrefilteredEnvRoughness", "Roughness/mip picked by the prefiltered-env debug view (0 = mirror, 1 = rough).");
			S("ForceIblRebake", "PERF: re-run the IBL bakes EVERY frame (ignores the once-baked gates). For iterating on the bake shaders — leave off.");

			EndGroup();
		}

		private void DrawReferences()
		{
			if (!BeginGroup("refs", "Shader & Texture References", defaultOpen: false)) { EndGroup(); return; }

			EditorGUILayout.HelpBox("Wired once when the asset is authored. Materials are created from these by the pipeline; " +
			                        "a null here disables (or breaks) the matching feature above.", MessageType.None);

			EditorGUILayout.LabelField("Present / AA", EditorStyles.boldLabel);
			R("_tonemapShader"); R("_fsrShader"); R("_smaaShader"); R("_smaaAreaTex"); R("_smaaSearchTex");

			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("Clustering (forward+)", EditorStyles.boldLabel);
			R("_clusterCullShader"); R("_clusterDebugShader"); R("_normalsDebugShader");

			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("Shadows", EditorStyles.boldLabel);
			R("_shadowMinMaxShader"); R("_screenSpaceShadowShader"); R("_temporalShadowShader"); R("_shadowBlurShader");

			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("SSAO", EditorStyles.boldLabel);
			R("_ssaoShader"); R("_gtaoShader"); R("_ssaoBlurShader"); R("_ssaoUpsampleShader"); R("_temporalAoShader"); R("_aoDebugShader");

			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("Radiance Cascades", EditorStyles.boldLabel);
			R("_radianceCascadeShader"); R("_rcCascadeShader"); R("_rcResolveShader"); R("_rcBlurShader"); R("_rcUpsampleShader"); R("_rcDebugShader");

			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("Transparency & Motion", EditorStyles.boldLabel);
			R("_oitCompositeShader"); R("_motionVectorsShader"); R("_motionVectorsDebugShader");

			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField("IBL", EditorStyles.boldLabel);
			R("_brdfLutShader"); R("_prefilterEnvShader"); R("_brdfLutDebugShader"); R("_prefilteredEnvDebugShader");
			R("_mainLightCookie");

			EndGroup();
		}

		// ---------------------------------------------------------------------------------------------
		// Helpers
		// ---------------------------------------------------------------------------------------------

		// Draw a PipelineSettings field (relative to the nested _settings) with a tooltip.
		private void S(string name, string tooltip)
		{
			SerializedProperty p = _settings.FindPropertyRelative(name);
			if (p == null)
			{
				EditorGUILayout.HelpBox($"Missing settings field: {name}", MessageType.Error);
				return;
			}
			EditorGUILayout.PropertyField(p, new GUIContent(p.displayName, tooltip));
		}

		// Draw a root asset field (shader / texture reference) by its serialized name.
		private void R(string name)
		{
			SerializedProperty p = serializedObject.FindProperty(name);
			if (p == null)
			{
				EditorGUILayout.HelpBox($"Missing asset field: {name}", MessageType.Error);
				return;
			}
			EditorGUILayout.PropertyField(p);
		}

		private bool B(string name)
		{
			SerializedProperty p = _settings.FindPropertyRelative(name);
			return p != null && p.boolValue;
		}

		private int E(string name)
		{
			SerializedProperty p = _settings.FindPropertyRelative(name);
			return p != null ? p.enumValueIndex : 0;
		}

		private void Perf(string text)
		{
			EditorGUILayout.HelpBox("PERFORMANCE\n" + text, MessageType.None);
		}

		private bool BeginGroup(string key, string title, bool defaultOpen = true)
		{
			bool state = EditorPrefs.GetBool(PrefPrefix + key, defaultOpen);
			bool now = EditorGUILayout.BeginFoldoutHeaderGroup(state, title);
			if (now != state)
				EditorPrefs.SetBool(PrefPrefix + key, now);
			if (now)
				EditorGUI.indentLevel++;
			return now;
		}

		private void EndGroup()
		{
			if (EditorGUI.indentLevel > 0)
				EditorGUI.indentLevel--;
			EditorGUILayout.EndFoldoutHeaderGroup();
			EditorGUILayout.Space(2);
		}
	}
}
