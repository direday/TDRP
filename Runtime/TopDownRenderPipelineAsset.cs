using UnityEngine;
using UnityEngine.Rendering;

namespace TDRP
{
	[CreateAssetMenu(menuName = "TDRP/Create CustomRenderPipelineAsset", fileName = "PipelineAsset", order = 0)]
	public class TopDownRenderPipelineAsset : RenderPipelineAsset<TopDownRenderPipeline>
	{
		[SerializeField] private PipelineSettings _settings = new();
		[SerializeField] private Shader _tonemapShader;
		[SerializeField] private ComputeShader _clusterCullShader;
		[SerializeField] private Shader _clusterDebugShader;
		[SerializeField] private Shader _normalsDebugShader;
		[SerializeField] private ComputeShader _shadowMinMaxShader;
		[SerializeField] private Shader _screenSpaceShadowShader;
		[SerializeField] private Shader _temporalShadowShader;
		[SerializeField] private Shader _shadowBlurShader;
		[SerializeField] private Shader _ssaoShader;
		[SerializeField] private Shader _gtaoShader;
		[SerializeField] private Shader _ssaoBlurShader;
		[SerializeField] private Shader _ssaoUpsampleShader;
		[SerializeField] private Shader _temporalAoShader;
		[SerializeField] private Shader _aoDebugShader;
		// Screen-space Radiance Cascades: the M1 single gather, the cascade-hierarchy build + resolve,
		// the bilateral denoiser, and the debug view.
		[SerializeField] private Shader _radianceCascadeShader;
		[SerializeField] private Shader _rcCascadeShader;
		[SerializeField] private Shader _rcResolveShader;
		[SerializeField] private Shader _rcBlurShader;
		[SerializeField] private Shader _rcUpsampleShader;
		[SerializeField] private Shader _rcTemporalShader;
		[SerializeField] private Shader _rcDebugShader;
		[SerializeField] private Shader _oitCompositeShader;
		[SerializeField] private Shader _motionVectorsShader;
		[SerializeField] private Shader _motionVectorsDebugShader;
		[SerializeField] private Shader _smaaShader;
		// FSR 1.0 spatial upscaler (EASU edge-adaptive upscale + RCAS sharpen), used when
		// PipelineSettings.Upscale = Fsr1 and RenderScale < 1.
		[SerializeField] private Shader _fsrShader;
		// Image-based lighting: the two precompute-once bakes (env-BRDF LUT + GGX prefilter of the
		// skybox reflection cube) and their debug views.
		[SerializeField] private ComputeShader _brdfLutShader;
		[SerializeField] private Shader _prefilterEnvShader; // raster per-face prefilter (not compute)
		[SerializeField] private Shader _brdfLutDebugShader;
		[SerializeField] private Shader _prefilteredEnvDebugShader;
		// Directional light cookie (a mask projected over the world to modulate the main light —
		// cloud shadows / gobo). Not turned into a material: bound directly as a global each frame by
		// SetupLightingPass, so swapping it applies live (no pipeline rebuild). Null = feature inert.
		[SerializeField] private Texture2D _mainLightCookie;
		// Precomputed SMAA lookup textures (baked from LUTs/AreaTex.h + SearchTex.h). AreaTex classifies
		// the coverage area of an edge pattern; SearchTex accelerates the edge-distance search.
		[SerializeField] private Texture2D _smaaAreaTex;
		[SerializeField] private Texture2D _smaaSearchTex;

		public PipelineSettings Settings => _settings;
		public Shader TonemapShader => _tonemapShader;
		public ComputeShader ClusterCullShader => _clusterCullShader;
		public Shader ClusterDebugShader => _clusterDebugShader;
		public Shader NormalsDebugShader => _normalsDebugShader;
		public ComputeShader ShadowMinMaxShader => _shadowMinMaxShader;
		public Shader ScreenSpaceShadowShader => _screenSpaceShadowShader;
		public Shader TemporalShadowShader => _temporalShadowShader;
		public Shader ShadowBlurShader => _shadowBlurShader;
		public Shader SsaoShader => _ssaoShader;
		public Shader GtaoShader => _gtaoShader;
		public Shader SsaoBlurShader => _ssaoBlurShader;
		public Shader SsaoUpsampleShader => _ssaoUpsampleShader;
		public Shader TemporalAoShader => _temporalAoShader;
		public Shader AoDebugShader => _aoDebugShader;
		public Shader RadianceCascadeShader => _radianceCascadeShader;
		public Shader RcCascadeShader => _rcCascadeShader;
		public Shader RcResolveShader => _rcResolveShader;
		public Shader RcBlurShader => _rcBlurShader;
		public Shader RcUpsampleShader => _rcUpsampleShader;
		public Shader RcTemporalShader => _rcTemporalShader;
		public Shader RcDebugShader => _rcDebugShader;
		public Shader OitCompositeShader => _oitCompositeShader;
		public Shader MotionVectorsShader => _motionVectorsShader;
		public Shader MotionVectorsDebugShader => _motionVectorsDebugShader;
		public Shader SmaaShader => _smaaShader;
		public Shader FsrShader => _fsrShader;
		public Texture2D SmaaAreaTex => _smaaAreaTex;
		public Texture2D SmaaSearchTex => _smaaSearchTex;
		public Texture2D MainLightCookie => _mainLightCookie;
		public ComputeShader BrdfLutShader => _brdfLutShader;
		public Shader PrefilterEnvShader => _prefilterEnvShader;
		public Shader BrdfLutDebugShader => _brdfLutDebugShader;
		public Shader PrefilteredEnvDebugShader => _prefilteredEnvDebugShader;

		// Declares which shader tag identifies "our" shaders. Unity matches this
		// against each SubShader's "RenderPipeline" = "..." tag. Left unoverridden
		// it returns string.Empty (and logs a warning), which mismatches our
		// shaders' "TopDownRenderPipeline" tag: Unity then won't associate those
		// SubShaders with this pipeline, so it never bakes an SRP Batcher layout
		// for them (and strips the variants in player builds).
		public override string renderPipelineShaderTag => "TopDownRenderPipeline";

		protected override RenderPipeline CreatePipeline()
		{
			return new TopDownRenderPipeline(this);
		}
	}
}
