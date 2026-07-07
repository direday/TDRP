using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	public class TopDownRenderPipeline : RenderPipeline, IRenderGraphEnabledRenderPipeline
	{
		public bool isImmediateModeSupported => true;
		
		private readonly RenderGraph _renderGraph = new("TDRP");
		private readonly RenderGraphResources _resources = new();

		// Hold the ASSET, not a snapshot of its settings, so tunable settings are read FRESH every
		// frame. PipelineSettings is a class stored inline in the asset; Unity can deserialize a NEW
		// instance into asset._settings on any reserialize (Inspector ApplyModifiedProperties, undo,
		// reimport, the assets-modify tool), which orphans a cached reference and makes live edits
		// silently stop applying. Re-fetching asset.Settings each frame always sees the current
		// object. _defaultSettings is the fallback for the (shouldn't-happen) null case.
		private readonly TopDownRenderPipelineAsset _asset;
		private readonly PipelineSettings _defaultSettings = new();
		private readonly Material _tonemapMaterial;
		private readonly Material _clusterDebugMaterial;
		private readonly Material _normalsDebugMaterial;
		private readonly Material _ssShadowMaterial;
		private readonly Material _temporalShadowMaterial;
		private readonly Material _shadowBlurMaterial;
		private readonly Material _ssaoMaterial;
		private readonly Material _gtaoMaterial;
		private readonly Material _ssaoBlurMaterial;
		private readonly Material _ssaoUpsampleMaterial;
		private readonly Material _temporalAoMaterial;
		private readonly Material _aoDebugMaterial;
		private readonly Material _radianceCascadeMaterial;
		private readonly Material _rcCascadeMaterial;
		private readonly Material _rcResolveMaterial;
		private readonly Material _rcBlurMaterial;
		private readonly Material _rcUpsampleMaterial;
		private readonly Material _rcTemporalMaterial;
		private readonly Material _rcDebugMaterial;
		private readonly Material _oitCompositeMaterial;
		private readonly Material _motionVectorsMaterial;
		private readonly Material _motionVectorsDebugMaterial;
		private readonly Material _smaaMaterial;
		private readonly Material _fsrMaterial;
		private readonly Material _prefilterEnvMaterial;
		private readonly Material _brdfLutDebugMaterial;
		private readonly Material _prefilteredEnvDebugMaterial;

		// Internal render counter, used to vary the per-frame shadow jitter. Not Time.frameCount:
		// that doesn't reliably advance on editor one-shot renders, which would freeze the jitter
		// (and so the accumulation) when inspecting via screenshots.
		private int _frameCounter;

		private readonly SetupLightingPass _lightingPass = new();
		private readonly ClusterLightCullPass _clusterCullPass;
		private readonly DirectionalShadowPass _shadowPass = new();
		private readonly DirectionalShadowMinMaxPass _shadowMinMaxPass;
		private readonly ScreenSpaceShadowPass _screenSpaceShadowPass;
		private readonly TemporalShadowPass _temporalShadowPass;
		private readonly ShadowBlurPass _shadowBlurPass;
		private readonly SsaoPass _ssaoPass;
		private readonly SsaoBlurPass _ssaoBlurPass;
		private readonly SsaoUpsamplePass _ssaoUpsamplePass;
		private readonly TemporalAoPass _temporalAoPass;
		private readonly RadianceCascadePass _radianceCascadePass;
		private readonly RcCascadeBuildPass _rcCascadeBuildPass;
		private readonly RcResolvePass _rcResolvePass;
		private readonly RcBlurPass _rcBlurPass;
		private readonly RcUpsamplePass _rcUpsamplePass;
		private readonly RcTemporalPass _rcTemporalPass;
		private readonly ColorHistoryCapturePass _colorHistoryCapturePass = new();
		private readonly ColorHistoryCache _colorHistoryCache = new("RcColorHistory");
		private readonly RcDebugPass _rcDebugPass;
		private readonly DepthNormalPrepassRenderPass _depthNormalPrepass = new();
		private readonly MotionVectorPass _motionVectorPass;
		private readonly MotionVectorsDebugPass _motionVectorsDebugPass;
		private readonly EnvironmentIBL _environmentIBL = new();
		private readonly EnvironmentIBLPass _environmentIBLPass;
		private readonly BrdfLutDebugPass _brdfLutDebugPass;
		private readonly PrefilteredEnvDebugPass _prefilteredEnvDebugPass;
		private readonly OpaqueRenderPass _opaquePass = new();
		private readonly SkyboxRenderPass _skyboxPass = new();
		private readonly TransparentRenderPass _transparentPass = new();
		private readonly WeightedOitAccumulatePass _oitAccumulatePass = new();
		private readonly WeightedOitCompositePass _oitCompositePass;
		private readonly ClusterDebugPass _clusterDebugPass;
		private readonly NormalsDebugPass _normalsDebugPass;
		private readonly AoDebugPass _aoDebugPass;
		private readonly TonemapPass _tonemapPass;
		private readonly SmaaEdgePass _smaaEdgePass;
		private readonly SmaaBlendWeightPass _smaaBlendWeightPass;
		private readonly SmaaBlendPass _smaaBlendPass;
		private readonly FsrUpscalePass _fsrUpscalePass;
		private readonly PresentPass _presentPass;

		public TopDownRenderPipeline(TopDownRenderPipelineAsset asset)
		{
			GraphicsSettings.useScriptableRenderPipelineBatching = true;

			_asset = asset;
			_tonemapMaterial = CoreUtils.CreateEngineMaterial(asset.TonemapShader);
			_ssShadowMaterial = CoreUtils.CreateEngineMaterial(asset.ScreenSpaceShadowShader);
			_temporalShadowMaterial = CoreUtils.CreateEngineMaterial(asset.TemporalShadowShader);
			if (asset.SsaoShader != null)
				_ssaoMaterial = CoreUtils.CreateEngineMaterial(asset.SsaoShader);
			if (asset.GtaoShader != null)
				_gtaoMaterial = CoreUtils.CreateEngineMaterial(asset.GtaoShader);
			if (asset.SsaoBlurShader != null)
				_ssaoBlurMaterial = CoreUtils.CreateEngineMaterial(asset.SsaoBlurShader);
			if (asset.SsaoUpsampleShader != null)
				_ssaoUpsampleMaterial = CoreUtils.CreateEngineMaterial(asset.SsaoUpsampleShader);
			if (asset.TemporalAoShader != null)
				_temporalAoMaterial = CoreUtils.CreateEngineMaterial(asset.TemporalAoShader);

			_clusterCullPass = new ClusterLightCullPass(asset.ClusterCullShader);
			_shadowMinMaxPass = new DirectionalShadowMinMaxPass(asset.ShadowMinMaxShader);
			_screenSpaceShadowPass = new ScreenSpaceShadowPass(_ssShadowMaterial);
			_temporalShadowPass = new TemporalShadowPass(_temporalShadowMaterial);
			if (asset.ShadowBlurShader != null)
				_shadowBlurMaterial = CoreUtils.CreateEngineMaterial(asset.ShadowBlurShader);
			_shadowBlurPass = new ShadowBlurPass(_shadowBlurMaterial);
			_ssaoPass = new SsaoPass(_ssaoMaterial, _gtaoMaterial);
			_ssaoBlurPass = new SsaoBlurPass(_ssaoBlurMaterial);
			_ssaoUpsamplePass = new SsaoUpsamplePass(_ssaoUpsampleMaterial);
			_temporalAoPass = new TemporalAoPass(_temporalAoMaterial);
			if (asset.ClusterDebugShader != null)
				_clusterDebugMaterial = CoreUtils.CreateEngineMaterial(asset.ClusterDebugShader);
			_clusterDebugPass = new ClusterDebugPass(_clusterDebugMaterial);

			if (asset.NormalsDebugShader != null)
				_normalsDebugMaterial = CoreUtils.CreateEngineMaterial(asset.NormalsDebugShader);
			_normalsDebugPass = new NormalsDebugPass(_normalsDebugMaterial);

			if (asset.AoDebugShader != null)
				_aoDebugMaterial = CoreUtils.CreateEngineMaterial(asset.AoDebugShader);
			_aoDebugPass = new AoDebugPass(_aoDebugMaterial);

			if (asset.RadianceCascadeShader != null)
				_radianceCascadeMaterial = CoreUtils.CreateEngineMaterial(asset.RadianceCascadeShader);
			_radianceCascadePass = new RadianceCascadePass(_radianceCascadeMaterial);
			if (asset.RcCascadeShader != null)
				_rcCascadeMaterial = CoreUtils.CreateEngineMaterial(asset.RcCascadeShader);
			_rcCascadeBuildPass = new RcCascadeBuildPass(_rcCascadeMaterial);
			if (asset.RcResolveShader != null)
				_rcResolveMaterial = CoreUtils.CreateEngineMaterial(asset.RcResolveShader);
			_rcResolvePass = new RcResolvePass(_rcResolveMaterial);
			if (asset.RcBlurShader != null)
				_rcBlurMaterial = CoreUtils.CreateEngineMaterial(asset.RcBlurShader);
			_rcBlurPass = new RcBlurPass(_rcBlurMaterial);
			if (asset.RcUpsampleShader != null)
				_rcUpsampleMaterial = CoreUtils.CreateEngineMaterial(asset.RcUpsampleShader);
			_rcUpsamplePass = new RcUpsamplePass(_rcUpsampleMaterial);
			if (asset.RcTemporalShader != null)
				_rcTemporalMaterial = CoreUtils.CreateEngineMaterial(asset.RcTemporalShader);
			_rcTemporalPass = new RcTemporalPass(_rcTemporalMaterial);
			if (asset.RcDebugShader != null)
				_rcDebugMaterial = CoreUtils.CreateEngineMaterial(asset.RcDebugShader);
			_rcDebugPass = new RcDebugPass(_rcDebugMaterial);

			if (asset.OitCompositeShader != null)
				_oitCompositeMaterial = CoreUtils.CreateEngineMaterial(asset.OitCompositeShader);
			_oitCompositePass = new WeightedOitCompositePass(_oitCompositeMaterial);

			if (asset.MotionVectorsShader != null)
				_motionVectorsMaterial = CoreUtils.CreateEngineMaterial(asset.MotionVectorsShader);
			_motionVectorPass = new MotionVectorPass(_motionVectorsMaterial);
			if (asset.MotionVectorsDebugShader != null)
				_motionVectorsDebugMaterial = CoreUtils.CreateEngineMaterial(asset.MotionVectorsDebugShader);
			_motionVectorsDebugPass = new MotionVectorsDebugPass(_motionVectorsDebugMaterial);

			if (asset.SmaaShader != null)
				_smaaMaterial = CoreUtils.CreateEngineMaterial(asset.SmaaShader);

			if (asset.FsrShader != null)
				_fsrMaterial = CoreUtils.CreateEngineMaterial(asset.FsrShader);

			if (asset.PrefilterEnvShader != null)
				_prefilterEnvMaterial = CoreUtils.CreateEngineMaterial(asset.PrefilterEnvShader);
			_environmentIBLPass = new EnvironmentIBLPass(asset.BrdfLutShader, _prefilterEnvMaterial);
			if (asset.BrdfLutDebugShader != null)
				_brdfLutDebugMaterial = CoreUtils.CreateEngineMaterial(asset.BrdfLutDebugShader);
			_brdfLutDebugPass = new BrdfLutDebugPass(_brdfLutDebugMaterial);
			if (asset.PrefilteredEnvDebugShader != null)
				_prefilteredEnvDebugMaterial = CoreUtils.CreateEngineMaterial(asset.PrefilteredEnvDebugShader);
			_prefilteredEnvDebugPass = new PrefilteredEnvDebugPass(_prefilteredEnvDebugMaterial);

			_tonemapPass = new TonemapPass(_tonemapMaterial);
			_smaaEdgePass = new SmaaEdgePass(_smaaMaterial);
			_smaaBlendWeightPass = new SmaaBlendWeightPass(_smaaMaterial, asset.SmaaAreaTex, asset.SmaaSearchTex);
			_smaaBlendPass = new SmaaBlendPass(_smaaMaterial);
			_fsrUpscalePass = new FsrUpscalePass(_fsrMaterial);
			_presentPass = new PresentPass();

			_renderGraph.RegisterDebug();
		}

		// Public entry point for game code to force the environment IBL to re-bake on the next frame —
		// e.g. on level load or after changing the skybox at runtime. A plain skybox swap is detected
		// automatically (the Material reference changes); use this when the reference stays the same but
		// the sky's appearance changed (procedural sun/exposure tweaked, or a scene-load handoff where you
		// want the bake guaranteed). One-shot: it re-bakes once, then the normal gates resume.
		public static void RequestEnvironmentRebake() => EnvironmentIBLPass.RequestRebake();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				_renderGraph.UnRegisterDebug();
				_renderGraph.Cleanup();
				_lightingPass.Dispose();
				CoreUtils.Destroy(_tonemapMaterial);
				CoreUtils.Destroy(_clusterDebugMaterial);
				CoreUtils.Destroy(_normalsDebugMaterial);
				CoreUtils.Destroy(_ssShadowMaterial);
				CoreUtils.Destroy(_temporalShadowMaterial);
				CoreUtils.Destroy(_shadowBlurMaterial);
				CoreUtils.Destroy(_ssaoMaterial);
				CoreUtils.Destroy(_gtaoMaterial);
				CoreUtils.Destroy(_ssaoBlurMaterial);
				CoreUtils.Destroy(_ssaoUpsampleMaterial);
				CoreUtils.Destroy(_temporalAoMaterial);
				CoreUtils.Destroy(_aoDebugMaterial);
				CoreUtils.Destroy(_radianceCascadeMaterial);
				CoreUtils.Destroy(_rcCascadeMaterial);
				CoreUtils.Destroy(_rcResolveMaterial);
				CoreUtils.Destroy(_rcBlurMaterial);
				CoreUtils.Destroy(_rcUpsampleMaterial);
				CoreUtils.Destroy(_rcTemporalMaterial);
				CoreUtils.Destroy(_rcDebugMaterial);
				CoreUtils.Destroy(_oitCompositeMaterial);
				CoreUtils.Destroy(_motionVectorsMaterial);
				CoreUtils.Destroy(_motionVectorsDebugMaterial);
				CoreUtils.Destroy(_smaaMaterial);
				CoreUtils.Destroy(_fsrMaterial);
				CoreUtils.Destroy(_prefilterEnvMaterial);
				CoreUtils.Destroy(_brdfLutDebugMaterial);
				CoreUtils.Destroy(_prefilteredEnvDebugMaterial);
				_temporalShadowPass.Dispose(); // releases the persistent history RTHandles
				_temporalAoPass.Dispose();     // releases the AO history RTHandles
				_rcTemporalPass.Dispose();     // releases the per-camera RC irradiance-history RTHandles
				_colorHistoryCache.Dispose();  // releases the per-camera RC color-history RTHandles
				_environmentIBL.Release();     // releases the persistent LUT / prefiltered-env RTHandles
			}

			base.Dispose(disposing);
		}

		protected override void Render(ScriptableRenderContext context, List<Camera> cameras)
		{
			foreach (var camera in cameras)
			{
				RenderCamera(context, camera);
			}

			_renderGraph.EndFrame();
		}

		private void RenderCamera(ScriptableRenderContext context, Camera camera)
		{
			if (!camera.TryGetCullingParameters(out var cullingParams))
				return;

			_frameCounter++;

			// Read the settings fresh from the asset this frame (see the _asset field comment): a
			// cached reference goes stale whenever Unity reserializes the asset's inline settings.
			var settings = _asset.Settings ?? _defaultSettings;

			// The shadow region is fit to this distance; Unity also uses it to cull
			// shadow casters during Cull(). Must be set before culling, not after.
			if (settings.MainLightShadows)
				cullingParams.shadowDistance = Mathf.Min(settings.ShadowDistance, camera.farClipPlane);

			var cullingResults = context.Cull(ref cullingParams);

			context.SetupCameraProperties(camera);

			// Internal render resolution: the camera's display pixel size scaled by RenderScale (clamped
			// so we never allocate a 0-sized buffer). Every offscreen buffer is sized to this; only the
			// present upscales back to the display size. Computed once here and carried on FrameContext so
			// every pass and allocation agrees on one number.
			float renderScale = Mathf.Clamp(settings.RenderScale, 0.25f, 2f);
			int renderWidth = Mathf.Max(1, Mathf.RoundToInt(camera.pixelWidth * renderScale));
			int renderHeight = Mathf.Max(1, Mathf.RoundToInt(camera.pixelHeight * renderScale));

			var frame = new FrameContext
			{
				Camera = camera,
				CullingResults = cullingResults,
				Settings = settings,
				FrameIndex = _frameCounter,
				RenderWidth = renderWidth,
				RenderHeight = renderHeight,
			};

			// Select the main directional light + fit its shadow matrices (CPU), then run
			// shadow-caster culling so the shadow renderer list has a valid caster set. This
			// must happen before recording (it needs the ScriptableRenderContext), and it's
			// the non-deprecated replacement for ShadowDrawingSettings.splitData.
			var mainLight = MainLightData.Create(in frame);
			if (mainLight.CastsShadows)
				CullShadowCasters(context, cullingResults, in mainLight);

			// Precompute-once IBL bakes (env-BRDF LUT now; prefiltered env cube later). Runs as an
			// immediate compute dispatch here — outside RenderGraph — because these persistent
			// resources are consumed via globals the graph can't see. Cheap after the first bake (the
			// LUT is environment-independent; the prefilter only re-runs on an environment change).
			_environmentIBLPass.BakeIfNeeded(context, _environmentIBL, camera, settings.ForceIblRebake);

			var cmd = CommandBufferPool.Get("TDRP");

			var rgParams = new RenderGraphParameters
			{
				executionId = camera.GetEntityId(),
				generateDebugData = RenderGraph.isRenderGraphViewerActive,
				commandBuffer = cmd,
				scriptableRenderContext = context,
				currentFrameIndex = Time.frameCount,
			};

			_renderGraph.BeginRecording(rgParams);
			RecordRenderGraph(_renderGraph, in frame, in mainLight);
			_renderGraph.EndRecordingAndExecute();

			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);

#if UNITY_EDITOR
			DrawGizmos(context, camera);
#endif

			context.Submit();
		}

		private static void CullShadowCasters(ScriptableRenderContext context,
			CullingResults cullingResults, in MainLightData mainLight)
		{
			var splitBuffer = new NativeArray<ShadowSplitData>(1, Allocator.Temp);
			splitBuffer[0] = mainLight.ShadowSplitData;

			// One entry per visible light; default (zero-length split range) = no shadows.
			var perLightInfos = new NativeArray<LightShadowCasterCullingInfo>(
				cullingResults.visibleLights.Length, Allocator.Temp);
			perLightInfos[mainLight.Index] = new LightShadowCasterCullingInfo
			{
				splitRange = new RangeInt(0, 1),
				projectionType = BatchCullingProjectionType.Orthographic,
			};

			context.CullShadowCasters(cullingResults, new ShadowCastersCullingInfos
			{
				splitBuffer = splitBuffer,
				perLightInfos = perLightInfos,
			});

			splitBuffer.Dispose();
			perLightInfos.Dispose();
		}

		private void RecordRenderGraph(RenderGraph rg, in FrameContext frame, in MainLightData mainLight)
		{
			AllocateResources(rg, in frame);

			// AO modulates shading only when SSAO is actually on (ShowAO alone produces the buffer
			// for the debug view but doesn't apply it). Known here so the lighting pass can compile
			// the _SCREEN_SPACE_AO keyword in/out before the opaque pass shades.
			bool ssaoActive = frame.Settings.Ssao && _ssaoPass.IsValid;

			// Screen-space Radiance Cascades: gather diffuse GI from the PREVIOUS frame's color. When the
			// feature is on we grab this camera's persistent color history now (allocating on first use)
			// so we know whether a previous frame exists. We only APPLY the gather (keyword on + record
			// the pass) once HasHistory is true; the very first frame(s) fall back to SH/ambient while the
			// history warms up (avoids a dark flash from sampling an empty buffer). The capture that fills
			// history runs after transparents, further below.
			int rcWidth = frame.RenderWidth;
			int rcHeight = frame.RenderHeight;
			var rcColorFormat = frame.Settings.UseHdr
				? GraphicsFormat.B10G11R11_UFloatPack32
				: GraphicsFormat.R8G8B8A8_SRGB;
			bool rcFeatureOn = frame.Settings.ScreenSpaceRadianceCascades && _radianceCascadePass.IsValid;
			ColorHistory rcHistory = null;
			RTHandle rcPrevColor = null;
			var rcHistoryWrite = TextureHandle.nullHandle;
			bool rcApply = false;
			if (rcFeatureOn)
			{
				rcHistory = _colorHistoryCache.Get(frame.Camera.GetEntityId(), _frameCounter);
				rcHistory.GetFrameTextures(rg, rcWidth, rcHeight, rcColorFormat, out rcPrevColor, out rcHistoryWrite);
				rcApply = rcHistory.HasHistory;
			}

			// Cookie texture comes straight from the (live) asset — swapping it applies without a
			// pipeline rebuild, since it's bound as a global rather than baked into a material.
			_lightingPass.Record(rg, in frame, in mainLight, ssaoActive, rcApply, _asset.MainLightCookie);

			// Bind the IBL globals + _ENV_IBL keyword (before opaque shading). Active only when the
			// feature is on AND the env resources are baked (LUT + prefiltered cube from a valid source).
			bool iblActive = frame.Settings.ImageBasedLighting && _environmentIBLPass.EnvReady
			                 && _environmentIBL.PrefilteredEnv != null && _environmentIBL.BrdfLut != null;
			_environmentIBLPass.Record(rg, _environmentIBL, in frame, iblActive);

			// Render the main directional shadow map (if the light casts shadows). The pass
			// binds it as a global texture the lit shaders sample; the keyword + matrix were
			// published by the lighting pass above.
			if (mainLight.CastsShadows)
			{
				int resolution = Mathf.Max(16, frame.Settings.ShadowResolution);
				resolution = Mathf.NextPowerOfTwo(resolution);
				_resources.DirectionalShadowMap = rg.CreateTexture(new TextureDesc(resolution, resolution)
				{
					depthBufferBits = DepthBits.Depth32,
					isShadowMap = true,
					clearBuffer = true,
					name = "MainLightShadowmap",
				});
				_shadowPass.Record(rg, in frame, in mainLight, _resources.DirectionalShadowMap);

				// Coarse min/max-depth level over the shadow map, used to early-out the PCSS
				// blocker search. Lossless — purely an acceleration, so it's gated on a setting.
				if (mainLight.UsePyramid && _shadowMinMaxPass.IsValid)
				{
					int coarse = mainLight.ShadowPyramidCoarseRes;
					_resources.ShadowMinMax = rg.CreateTexture(new TextureDesc(coarse, coarse)
					{
						format = GraphicsFormat.R32G32_SFloat,
						enableRandomWrite = true,
						name = "MainLightShadowMinMax",
					});
					_shadowMinMaxPass.Record(rg, _resources.DirectionalShadowMap, _resources.ShadowMinMax,
						MainLightData.ShadowPyramidBlock, resolution, coarse);
				}
			}

			_clusterCullPass.Record(rg, in frame, _lightingPass.PunctualLightBuffer, _lightingPass.PunctualLightCount,
				_resources.ClusterLightGrid, _resources.ClusterLightIndices);
			// Depth + world-space normal in one prepass (MRT). Depth feeds early-Z + world-pos
			// reconstruction; the shared normal buffer feeds the SS shadow mask (normal bias) and
			// SSAO. Both are published as globals by the pass.
			_depthNormalPrepass.Record(rg, in frame, _resources.CameraDepth, _resources.CameraNormals);

			// Screen-space velocity buffer. Built when motion vectors are on (a later step feeds it
			// into the temporal AO / shadow reprojection) or when only its debug view is requested.
			// Runs right after the prepass: the fullscreen camera-motion pass reads CameraDepth, and
			// the per-object pass ZTests Equal against it. RG16_SFloat holds the signed UV-space delta;
			// no clear needed — the fullscreen pass writes every pixel.
			bool motionVectorsActive = (frame.Settings.MotionVectors || frame.Settings.ShowMotionVectors)
			                           && _motionVectorPass.IsValid;
			if (motionVectorsActive)
			{
				_resources.CameraMotionVectors = rg.CreateTexture(new TextureDesc(
					frame.RenderWidth, frame.RenderHeight)
				{
					format = GraphicsFormat.R16G16_SFloat,
					name = "CameraMotionVectors",
				});
				_motionVectorPass.Record(rg, in frame, _resources.CameraMotionVectors, _resources.CameraDepth);
			}

			// Screen-space AO from depth+normal. Built when SSAO is on (it will modulate the ambient
			// term) or when only its debug view is requested. The buffer is R16G16 (R: occlusion,
			// G: eye depth) so the bilateral blur — and later the temporal resolve — can stop at
			// depth edges. The final result is published as the global _ScreenSpaceAO.
			if ((frame.Settings.Ssao || frame.Settings.ShowAO) && _ssaoPass.IsValid)
			{
				int fullWidth = frame.RenderWidth;
				int fullHeight = frame.RenderHeight;

				// Estimate / blur / temporal run at half res (4x fewer pixels) when enabled, then a
				// joint-bilateral upsample brings the result back to full res for the lit shader.
				bool halfRes = frame.Settings.SsaoHalfResolution && _ssaoUpsamplePass.IsValid;
				int aoWidth = halfRes ? (fullWidth + 1) / 2 : fullWidth;
				int aoHeight = halfRes ? (fullHeight + 1) / 2 : fullHeight;

				// AO working buffers: R = occlusion, G = eye depth (for the bilateral + temporal).
				// No clear: the estimate + separable blur are fullscreen passes whose fragment
				// always returns — the background branch (SSAO.shader) already writes (1,0) for
				// texels with no geometry, so a clear would only duplicate what the shader writes.
				TextureDesc AoDesc(string name) => new(aoWidth, aoHeight)
				{
					format = GraphicsFormat.R16G16_SFloat,
					name = name,
				};

				bool temporal = frame.Settings.SsaoTemporal && _temporalAoPass.IsValid;
				// Per-frame rotation jitter so each frame's AO is decorrelated -> accumulation
				// averages it. Golden-ratio sequence on the render counter (not Time.frameCount,
				// which doesn't advance on one-shot editor renders). Zero when not accumulating.
				float jitter = temporal ? (float)((_frameCounter * 0.61803398875) % 1.0) : 0f;

				bool useGtao = frame.Settings.AoMethod == AmbientOcclusionMethod.GroundTruth;
				var rawAo = rg.CreateTexture(AoDesc("ScreenSpaceAO"));
				_ssaoPass.Record(rg, in frame, _resources.CameraDepth, _resources.CameraNormals, rawAo, jitter, useGtao);

				var ao = rawAo;
				if (frame.Settings.SsaoDenoise && _ssaoBlurPass.IsValid)
				{
					// Separable cross-bilateral: horizontal then vertical, each republishing
					// _ScreenSpaceAO so the next pass reads the latest.
					var blurH = rg.CreateTexture(AoDesc("ScreenSpaceAO_BlurH"));
					var blurV = rg.CreateTexture(AoDesc("ScreenSpaceAO_Blur"));
					_ssaoBlurPass.Record(rg, in frame, rawAo, _resources.CameraNormals, blurH, false, aoWidth, aoHeight);
					_ssaoBlurPass.Record(rg, in frame, blurH, _resources.CameraNormals, blurV, true, aoWidth, aoHeight);
					ao = blurV;
				}

				// Temporal accumulation: blend the (jittered) current AO with reprojected history.
				if (temporal)
				{
					_temporalAoPass.Record(rg, in frame, ao, _resources.CameraDepth,
						_resources.CameraMotionVectors, frame.Settings.SsaoTemporalAlpha, aoWidth, aoHeight,
						out var resolvedAo);
					ao = resolvedAo;
				}

				// Bring half-res AO back to full res (depth-aware) — becomes the full-res
				// _ScreenSpaceAO the lit shader LOADs by pixel.
				if (halfRes)
				{
					var upsampled = rg.CreateTexture(new TextureDesc(fullWidth, fullHeight)
					{
						format = GraphicsFormat.R8_UNorm,
						name = "ScreenSpaceAO_Full",
					});
					_ssaoUpsamplePass.Record(rg, in frame, ao, _resources.CameraDepth, _resources.CameraNormals, upsampled, aoWidth, aoHeight);
					ao = upsampled;
				}

				_resources.AmbientOcclusion = ao;
			}

			// Resolve the main directional shadow into a screen-space mask — after depth exists
			// (it reconstructs world pos from depth), before shading (which samples the mask).
			if (mainLight.UseScreenSpaceShadows && _screenSpaceShadowPass.IsValid)
			{
				bool temporal = mainLight.UseTemporalShadows && _temporalShadowPass.IsValid;

				int width = frame.RenderWidth;
				int height = frame.RenderHeight;
				// No clear: this is a fullscreen pass whose fragment always returns — the
				// background branch (ScreenSpaceShadows.shader) writes fully-lit for texels with
				// no geometry, so a clear would only duplicate what the shader already writes.
				var rawMask = rg.CreateTexture(new TextureDesc(width, height)
				{
					format = GraphicsFormat.R8_UNorm,
					name = temporal ? "ScreenShadowRaw" : "ScreenShadowMask",
				});

				// Golden-ratio sequence -> well-distributed per-frame disk rotations so accumulation
				// averages different noise each frame. Only varied when actually accumulating.
				float jitter = temporal ? (float)((_frameCounter * 0.61803398875) % 1.0) : 0f;
				_screenSpaceShadowPass.Record(rg, in frame, _resources.CameraDepth, _resources.CameraNormals,
					_resources.DirectionalShadowMap, _resources.ShadowMinMax, rawMask, temporal, jitter,
					frame.Settings.PcssTapCount);

				if (temporal)
				{
					// Blend with reprojected history; the resolved buffer becomes the sampled mask.
					_temporalShadowPass.Record(rg, in frame, rawMask, _resources.CameraDepth,
						_resources.CameraMotionVectors, frame.Settings.TemporalShadowAlpha,
						out _resources.ScreenShadowMask);
				}
				else
				{
					_resources.ScreenShadowMask = rawMask;
				}

				// Spatial denoise: variance-modulated depth/normal bilateral on the resolved mask.
				// The non-temporal way to clean few-tap PCSS dither (no motion ghosting); composes on
				// top of the temporal resolve if both are on. Separable H then V, each republishing
				// _ScreenShadowMask so the lit shader samples the final blurred result.
				if (frame.Settings.ShadowDenoise && _shadowBlurPass.IsValid
				    && _resources.ScreenShadowMask.IsValid())
				{
					int radius = frame.Settings.ShadowBlurRadius;
					float sensitivity = frame.Settings.ShadowBlurVarianceSensitivity;

					var blurH = rg.CreateTexture(new TextureDesc(width, height)
					{
						format = GraphicsFormat.R8_UNorm,
						name = "ScreenShadowBlurH",
					});
					var blurV = rg.CreateTexture(new TextureDesc(width, height)
					{
						format = GraphicsFormat.R8_UNorm,
						name = "ScreenShadowMaskBlurred",
					});

					_shadowBlurPass.Record(rg, in frame, _resources.ScreenShadowMask, _resources.CameraDepth,
						_resources.CameraNormals, blurH, false, radius, sensitivity, width, height);
					_shadowBlurPass.Record(rg, in frame, blurH, _resources.CameraDepth,
						_resources.CameraNormals, blurV, true, radius, sensitivity, width, height);
					_resources.ScreenShadowMask = blurV;
				}
			}

			// Radiance Cascades diffuse-GI gather: marches this frame's depth/normals but samples radiance
			// from the previous frame's color (rcPrevColor), producing the full-res _RcIrradiance the lit
			// shader consumes. Runs after the depth+normal prepass, before opaque shading. Only when a
			// previous frame exists (rcApply) — else the lit shader falls back to SH/ambient this frame.
			if (rcApply)
			{
				// Process (build + resolve + blur) at half res when enabled, then a depth-aware bilateral
				// upsample back to full res — GI is low-frequency so this is ~free quality-wise (like SSAO).
				bool half = frame.Settings.RcHalfResolution && _rcUpsamplePass.IsValid;
				int procW = half ? (rcWidth + 1) / 2 : rcWidth;
				int procH = half ? (rcHeight + 1) / 2 : rcHeight;

				TextureDesc RcDesc(string name, int w, int h) => new(w, h)
				{
					format = GraphicsFormat.B10G11R11_UFloatPack32, // positive HDR irradiance
					name = name,
				};

				var rawRc = rg.CreateTexture(RcDesc("RcIrradiance", procW, procH));
				// Two producers of the raw irradiance: the full cascade HIERARCHY (multi-scale probes,
				// merged near->far — long range at log cost) or the M1 single gather (RcMaxDistance-
				// capped). Both work at any resolution and feed the same denoise + upsample + apply.
				bool useCascades = frame.Settings.RcUseCascades
				                   && _rcCascadeBuildPass.IsValid && _rcResolvePass.IsValid;
				if (useCascades)
				{
					RecordRadianceCascades(rg, in frame, rcPrevColor, rawRc, procW, procH);
				}
				else
				{
					// frameJitter 0: no temporal accumulation (the ray fan uses only the per-pixel
					// rotation). Plumbed for a later temporal milestone.
					_radianceCascadePass.Record(rg, in frame, _resources.CameraDepth, _resources.CameraNormals,
						rcPrevColor, rawRc, 0f);
				}

				var rc = rawRc;
				// Spatial denoise: separable depth/normal cross-bilateral over the RGB irradiance (GI is
				// low-frequency, so a wide blur cleans the ray-fan noise with no ghosting). Each axis
				// republishes _RcIrradiance so the next pass — and the lit shader — reads the latest.
				if (frame.Settings.RcDenoise && _rcBlurPass.IsValid)
				{
					int radius = frame.Settings.RcBlurRadius;
					var blurH = rg.CreateTexture(RcDesc("RcIrradiance_BlurH", procW, procH));
					var blurV = rg.CreateTexture(RcDesc("RcIrradiance_Blur", procW, procH));
					_rcBlurPass.Record(rg, in frame, rawRc, _resources.CameraDepth, _resources.CameraNormals,
						blurH, false, radius, procW, procH);
					_rcBlurPass.Record(rg, in frame, blurH, _resources.CameraDepth, _resources.CameraNormals,
						blurV, true, radius, procW, procH);
					rc = blurV;
				}

				// Temporal accumulation: reproject last frame's irradiance and blend, to smooth the
				// screen-space probe-grid crawl on camera/object motion. After the spatial blur (mirrors
				// the SSAO order: denoise then accumulate) and before upsample, so it runs at the RC
				// processing resolution. Uses motion vectors when the pipeline produced them this frame
				// (movers reproject correctly), else camera-only reprojection.
				if (frame.Settings.RcTemporal && _rcTemporalPass.IsValid)
				{
					_rcTemporalPass.Record(rg, in frame, rc, _resources.CameraDepth,
						_resources.CameraMotionVectors, frame.Settings.RcTemporalAlpha,
						frame.Settings.RcTemporalResponse, procW, procH, out var resolvedRc);
					rc = resolvedRc;
				}

				// Depth-aware upsample half-res -> full res for the lit shader (LOADs it per pixel).
				if (half)
				{
					var full = rg.CreateTexture(RcDesc("RcIrradiance_Full", rcWidth, rcHeight));
					_rcUpsamplePass.Record(rg, in frame, rc, _resources.CameraDepth, _resources.CameraNormals, full, procW, procH);
					rc = full;
				}

				_resources.RcIrradiance = rc;
			}

			_opaquePass.Record(rg, in frame, _resources.CameraColor, _resources.CameraDepth,
				_resources.ClusterLightGrid, _resources.ClusterLightIndices, _resources.ScreenShadowMask,
				ssaoActive ? _resources.AmbientOcclusion : TextureHandle.nullHandle,
				rcApply ? _resources.RcIrradiance : TextureHandle.nullHandle);

			if (frame.Camera.clearFlags == CameraClearFlags.Skybox && RenderSettings.skybox != null)
			{
				_skyboxPass.Record(rg, in frame, _resources.CameraColor, _resources.CameraDepth);
			}

			// Capture this frame's OPAQUE+skybox HDR scene color into the color history so NEXT frame's RC
			// gather can sample it as incoming radiance. Deliberately BEFORE transparents: screen-space GI
			// wants an opaque-only source (as URP/HDRP SSGI/SSR do). Transparents — especially bright unlit /
			// additive particles — have no reliable depth/normal to act as emitters, and feeding them back as
			// radiance produces halos and firefly noise through the cascades. RenderGraph versions
			// CameraColor: this copy reads the post-skybox version while the transparent passes below write a
			// later version onto the same buffer, so the final image is unchanged and the GI snapshot simply
			// holds the pre-transparent subset. Still linear HDR here (before tonemap). Advance() flips the
			// ping-pong so what we wrote becomes next frame's read.
			if (rcFeatureOn)
			{
				_colorHistoryCapturePass.Record(rg, _resources.CameraColor, rcHistoryWrite);
				rcHistory.Advance();
			}

			// Transparents last (over opaque + skybox), depth read-only. Either the classic
			// back-to-front sorted pass, or the order-independent WBOIT accumulate+composite pair.
			bool useOit = frame.Settings.TransparencyMode == TransparencyMode.WeightedBlended
			              && _oitCompositePass.IsValid;
			if (useOit)
			{
				int width = frame.RenderWidth;
				int height = frame.RenderHeight;

				// Accum: weighted sum of premultiplied color + weight. RGBA16F for the HDR range
				// and the large weights; cleared to 0 (empty sum).
				_resources.OitAccum = rg.CreateTexture(new TextureDesc(width, height)
				{
					format = GraphicsFormat.R16G16B16A16_SFloat,
					clearBuffer = true,
					clearColor = Color.clear,
					name = "OitAccum",
				});
				// Revealage: running product of (1 - alpha). Cleared to 1 (nothing covered yet);
				// the Blend Zero OneMinusSrcColor in the accumulation pass multiplies it down.
				_resources.OitRevealage = rg.CreateTexture(new TextureDesc(width, height)
				{
					format = GraphicsFormat.R8_UNorm,
					clearBuffer = true,
					clearColor = Color.white,
					name = "OitRevealage",
				});

				_oitAccumulatePass.Record(rg, in frame, _resources.OitAccum, _resources.OitRevealage,
					_resources.CameraDepth, _resources.ClusterLightGrid, _resources.ClusterLightIndices);
				_oitCompositePass.Record(rg, in frame, _resources.CameraColor,
					_resources.OitAccum, _resources.OitRevealage);
			}
			else
			{
				_transparentPass.Record(rg, in frame, _resources.CameraColor, _resources.CameraDepth,
					_resources.ClusterLightGrid, _resources.ClusterLightIndices);
			}

			var presentSource = _resources.CameraColor;
			if (frame.Settings.ShowRcRadiance && _rcDebugPass.IsValid && _resources.RcIrradiance.IsValid())
			{
				var debugColor = rg.CreateTexture(new TextureDesc(
					frame.RenderWidth, frame.RenderHeight)
				{
					format = frame.Settings.UseHdr
						? GraphicsFormat.B10G11R11_UFloatPack32
						: GraphicsFormat.R8G8B8A8_SRGB,
					name = "RcDebugColor",
				});

				_rcDebugPass.Record(rg, in frame, _resources.RcIrradiance, debugColor);
				presentSource = debugColor;
			}
			else if (frame.Settings.ShowBrdfLut && _brdfLutDebugPass.IsValid && _environmentIBL.BrdfLut != null)
			{
				var debugColor = rg.CreateTexture(new TextureDesc(
					frame.RenderWidth, frame.RenderHeight)
				{
					format = frame.Settings.UseHdr
						? GraphicsFormat.B10G11R11_UFloatPack32
						: GraphicsFormat.R8G8B8A8_SRGB,
					name = "BrdfLutDebugColor",
				});

				_brdfLutDebugPass.Record(rg, in frame, _environmentIBL.BrdfLut, debugColor);
				presentSource = debugColor;
			}
			else if (frame.Settings.ShowPrefilteredEnv && _prefilteredEnvDebugPass.IsValid
			    && _environmentIBLPass.EnvReady && _environmentIBL.PrefilteredEnv != null)
			{
				var debugColor = rg.CreateTexture(new TextureDesc(
					frame.RenderWidth, frame.RenderHeight)
				{
					format = frame.Settings.UseHdr
						? GraphicsFormat.B10G11R11_UFloatPack32
						: GraphicsFormat.R8G8B8A8_SRGB,
					name = "PrefilteredEnvDebugColor",
				});

				float mip = frame.Settings.ShowPrefilteredEnvRoughness * _environmentIBL.CubeMaxMip;
				_prefilteredEnvDebugPass.Record(rg, in frame, _environmentIBL.PrefilteredEnv, mip, debugColor);
				presentSource = debugColor;
			}
			else if (frame.Settings.ShowMotionVectors && _motionVectorsDebugPass.IsValid
			    && _resources.CameraMotionVectors.IsValid())
			{
				var debugColor = rg.CreateTexture(new TextureDesc(
					frame.RenderWidth, frame.RenderHeight)
				{
					format = frame.Settings.UseHdr
						? GraphicsFormat.B10G11R11_UFloatPack32
						: GraphicsFormat.R8G8B8A8_SRGB,
					name = "MotionVectorsDebugColor",
				});

				_motionVectorsDebugPass.Record(rg, in frame, _resources.CameraMotionVectors, debugColor);
				presentSource = debugColor;
			}
			else if (frame.Settings.ShowAO && _aoDebugPass.IsValid && _resources.AmbientOcclusion.IsValid())
			{
				var debugColor = rg.CreateTexture(new TextureDesc(
					frame.RenderWidth, frame.RenderHeight)
				{
					format = frame.Settings.UseHdr
						? GraphicsFormat.B10G11R11_UFloatPack32
						: GraphicsFormat.R8G8B8A8_SRGB,
					name = "AoDebugColor",
				});

				_aoDebugPass.Record(rg, in frame, _resources.AmbientOcclusion, debugColor);
				presentSource = debugColor;
			}
			else if (frame.Settings.ShowNormals && _normalsDebugPass.IsValid)
			{
				var debugColor = rg.CreateTexture(new TextureDesc(
					frame.RenderWidth, frame.RenderHeight)
				{
					format = frame.Settings.UseHdr
						? GraphicsFormat.B10G11R11_UFloatPack32
						: GraphicsFormat.R8G8B8A8_SRGB,
					name = "NormalsDebugColor",
				});

				_normalsDebugPass.Record(rg, in frame, _resources.CameraNormals, debugColor);
				presentSource = debugColor;
			}
			else if (frame.Settings.ShowClusterHeatmap && _clusterCullPass.IsValid && _clusterDebugMaterial != null)
			{
				var debugColor = rg.CreateTexture(new TextureDesc(
					frame.RenderWidth, frame.RenderHeight)
				{
					format = frame.Settings.UseHdr
						? GraphicsFormat.B10G11R11_UFloatPack32
						: GraphicsFormat.R8G8B8A8_SRGB,
					name = "ClusterDebugColor",
				});

				_clusterDebugPass.Record(rg, in frame, _resources.CameraColor, _resources.ClusterLightGrid, debugColor);
				presentSource = debugColor;
			}

			// Tonemap the (HDR) scene — or the selected debug view — into a sampleable LDR buffer, then
			// present that to the backbuffer. Present is an unsafe cmd.Blit to CameraTarget, which tolerates
			// the target's real size — so when RenderScale < 1 the render-res LDR is upscaled to the display
			// here (Present is the render->display upscale point). All intermediates are at render res.
			_resources.CameraColorLdr = rg.CreateTexture(new TextureDesc(frame.RenderWidth, frame.RenderHeight)
			{
				// sRGB storage holds the tonemapped perceptual values (also what SMAA edge detection reads).
				// No clear: the tonemap blit writes every pixel.
				format = GraphicsFormat.R8G8B8A8_SRGB,
				name = "CameraColorLdr",
			});

			_tonemapPass.Record(rg, in frame, presentSource, _resources.CameraColorLdr);

			// Post-tonemap SMAA (edge detect -> blend weights -> neighborhood blend), at render res. The
			// full chain runs when SMAA is the active AA mode; when only the edge debug view is requested we
			// run just the edge pass and present its mask raw.
			int aaW = frame.RenderWidth;
			int aaH = frame.RenderHeight;
			var finalLdr = _resources.CameraColorLdr;
			bool smaaActive = frame.Settings.AntiAliasing == AntiAliasingMode.Smaa
			                  && _smaaEdgePass.IsValid && _smaaBlendWeightPass.IsValid && _smaaBlendPass.IsValid;
			if (smaaActive || (frame.Settings.ShowSmaaEdges && _smaaEdgePass.IsValid))
			{
				// R = horizontal edge, G = vertical edge. Cleared to 0 so discarded (non-edge) pixels
				// read empty.
				_resources.SmaaEdges = rg.CreateTexture(new TextureDesc(aaW, aaH)
				{
					format = GraphicsFormat.R8G8_UNorm,
					clearBuffer = true,
					clearColor = Color.clear,
					name = "SmaaEdges",
				});
				_smaaEdgePass.Record(rg, in frame, _resources.CameraColorLdr, _resources.CameraNormals, _resources.SmaaEdges);

				if (frame.Settings.ShowSmaaEdges)
				{
					// Debug: present the edge mask directly (post-tonemap, so a raw copy).
					finalLdr = _resources.SmaaEdges;
				}
				else // smaaActive
				{
					// Per-pixel blend weights (RGBA). Cleared to 0 so non-edge pixels blend nothing.
					_resources.SmaaWeights = rg.CreateTexture(new TextureDesc(aaW, aaH)
					{
						format = GraphicsFormat.R8G8B8A8_UNorm,
						clearBuffer = true,
						clearColor = Color.clear,
						name = "SmaaWeights",
					});
					_smaaBlendWeightPass.Record(rg, in frame, _resources.SmaaEdges, _resources.SmaaWeights);

					// Final anti-aliased LDR (sRGB, like CameraColorLdr, so Present copies it identically).
					var aaColor = rg.CreateTexture(new TextureDesc(aaW, aaH)
					{
						format = GraphicsFormat.R8G8B8A8_SRGB,
						name = "CameraColorAA",
					});
					_smaaBlendPass.Record(rg, in frame, _resources.CameraColorLdr, _resources.SmaaWeights, aaColor);
					finalLdr = aaColor;
				}
			}

			// Render-res -> display upscale. When RenderScale < 1, FSR 1 (EASU edge-adaptive upscale +
			// RCAS sharpen) brings the image back up to display res edge-aware, instead of the plain
			// bilinear stretch Present's blit does. Ordered AFTER SMAA (AMD wants a clean AA'd input to a
			// spatial upscaler). Only when actually upscaling: at RenderScale >= 1 there's nothing to
			// upscale, so we leave finalLdr at render res and let Present do the 1:1 copy / supersample
			// downscale. finalLdr becomes the display-res sharpened image that Present blits.
			if (frame.Settings.Upscale == UpscaleMode.Fsr1 && _fsrUpscalePass.IsValid
			    && frame.RenderWidth < frame.Camera.pixelWidth)
			{
				finalLdr = _fsrUpscalePass.Record(rg, in frame, finalLdr,
					frame.Camera.pixelWidth, frame.Camera.pixelHeight);
			}

			_presentPass.Record(rg, in frame, finalLdr);

			_resources.Reset();
		}

		// Builds the Radiance Cascades hierarchy and resolves it into `rawRc` (full-res irradiance). Each
		// cascade i has probe spacing p0*2^i and 4^(i+1) directions packed into a d x d block (d=2^(i+1)),
		// so all cascade textures stay ~constant size. Intervals grow x4 per level (t[i+1]=t[i]+l0*4^i),
		// so N cascades reach ~l0*(4^N-1)/3 world units at near-constant per-cascade cost. Built top-down
		// so each lower cascade continues its missed rays into the merged cascade above.
		private void RecordRadianceCascades(RenderGraph rg, in FrameContext frame, RTHandle prevColor,
			TextureHandle rawRc, int width, int height)
		{
			var settings = frame.Settings;
			int count = Mathf.Clamp(settings.RcCascadeCount, 1, 6);
			int p0 = Mathf.Max(1, settings.RcProbeSpacing);
			float l0 = Mathf.Max(0.01f, settings.RcCascadeInterval0);
			int steps = Mathf.Max(1, settings.RcStepCount);
			float bias = Mathf.Max(0f, settings.RcDepthBias);
			float skyFloor = Mathf.Clamp01(settings.RcSkyVisibility);
			float falloff = Mathf.Max(0f, settings.RcFalloffDistance);
			float fireflyClamp = Mathf.Max(0f, settings.RcFireflyClamp);

			// Base block side: d0 = sqrt(D0). Only perfect squares are representable in the d x d atlas,
			// so RcBaseDirections is 4 (d0=2) or 16 (d0=4). More directions = fewer spokes/halos.
			int d0Base = settings.RcBaseDirections >= 16 ? 4 : 2;

			var d = new int[count];
			var s = new int[count];
			var dirs = new int[count];
			var texW = new int[count];
			var texH = new int[count];
			var t = new float[count + 1];
			var cascade = new TextureHandle[count];
			t[0] = 0f;
			for (int i = 0; i < count; i++)
			{
				d[i] = d0Base << i; // d0 * 2^i  (d0 = 2 for 4 dirs, 4 for 16 dirs)
				s[i] = p0 << i;     // p0 * 2^i
				dirs[i] = d[i] * d[i];
				int gw = (width + s[i] - 1) / s[i];  // probe grid (ceil)
				int gh = (height + s[i] - 1) / s[i];
				texW[i] = gw * d[i];
				texH[i] = gh * d[i];
				t[i + 1] = t[i] + l0 * Mathf.Pow(4f, i);

				cascade[i] = rg.CreateTexture(new TextureDesc(texW[i], texH[i])
				{
					format = GraphicsFormat.R16G16B16A16_SFloat, // per-direction HDR radiance
					name = $"RcCascade{i}",
				});
			}

			// Previous frame's view-proj for this camera, for reprojecting the bounce-colour fetch (cancels
			// camera-motion lag in the GI). Computed once and shared by every cascade this frame; must
			// match the build pass's own viewProj formula so this frame's marched VP is next frame's prev.
			var rcViewProj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true) * frame.Camera.worldToCameraMatrix;
			var rcPrevViewProj = _rcCascadeBuildPass.UpdateAndGetPrevViewProj(frame.Camera.GetEntityId(), rcViewProj);

			// Per-frame sub-cell probe jitter, MOTION-GATED. Jittering the probe grid lets temporal
			// accumulation reconstruct a denser effective grid, dissolving the fixed-grid crawl. But the
			// crawl and the jitter are the SAME quantity (probe-value variation across a cell), so at a
			// static camera full-cell jitter just "breathes" — there's no motion to hide it and no crawl to
			// fix. Crawl only appears while the projected image MOVES, so we scale the jitter by camera
			// motion: ~0 at rest (clean), ramping to full while panning (crawl masked by the motion). Motion
			// is measured scale-free as the NDC displacement of a few static mid-depth points between last
			// frame's and this frame's view-proj (prevVP^-1 -> world -> curVP); an unmoved camera gives the
			// identity map -> 0. R2 (plastic-number) low-discrepancy offset so the covered positions spread
			// evenly. Only when temporal is on to average it; build/merge/resolve all consume this same offset.
			Vector2 rcJitter = Vector2.zero;
			float jitterScale = Mathf.Clamp01(settings.RcJitterScale);
			if (settings.RcTemporal && _rcTemporalPass.IsValid && jitterScale > 0f)
			{
				Matrix4x4 reproj = rcViewProj * rcPrevViewProj.inverse; // prev-clip -> cur-clip, static point
				float maxDisp = 0f;
				// screen centre + 4 corners at NDC z=0; how far each moves in NDC = scale-free screen motion.
				Vector2[] ndcPts = { new(0f, 0f), new(-1f, -1f), new(1f, -1f), new(-1f, 1f), new(1f, 1f) };
				foreach (var p in ndcPts)
				{
					Vector4 clip = reproj * new Vector4(p.x, p.y, 0f, 1f);
					if (Mathf.Abs(clip.w) < 1e-5f) continue;
					var moved = new Vector2(clip.x / clip.w - p.x, clip.y / clip.w - p.y);
					maxDisp = Mathf.Max(maxDisp, moved.magnitude);
				}
				// Ramp 0->1 over a sub-pixel-to-~1px NDC band: any deliberate pan is fully jittered; true
				// rest (identity reproj -> maxDisp 0) stays at 0. Thresholds are in NDC (screen-relative).
				float motion = Mathf.SmoothStep(0.0005f, 0.004f, maxDisp);
				if (motion > 0f)
				{
					const double g = 1.32471795724474602596; // plastic number: R2 sequence base
					double a1 = 1.0 / g, a2 = 1.0 / (g * g);
					float mag = motion * jitterScale;
					rcJitter = new Vector2(
						(float)((0.5 + a1 * _frameCounter) % 1.0) - 0.5f,
						(float)((0.5 + a2 * _frameCounter) % 1.0) - 0.5f) * mag;
				}
			}

			// Top-down: highest cascade first (no upper), each lower one merges the cascade above.
			for (int i = count - 1; i >= 0; i--)
			{
				bool hasUpper = i < count - 1;
				var desc = new RcCascadeBuildPass.CascadeDesc
				{
					Index = i,
					BlockSide = d[i],
					Spacing = s[i],
					Directions = dirs[i],
					IntervalStart = t[i],
					IntervalEnd = t[i + 1],
					Steps = steps,
					Merge = settings.RcCascadeMerge,
					UpperBlockSide = hasUpper ? d[i + 1] : 0,
					UpperSpacing = hasUpper ? s[i + 1] : 0,
					UpperTexW = hasUpper ? texW[i + 1] : 0,
					UpperTexH = hasUpper ? texH[i + 1] : 0,
				};
				_rcCascadeBuildPass.Record(rg, in frame, in desc, cascade[i],
					hasUpper ? cascade[i + 1] : TextureHandle.nullHandle,
					_resources.CameraDepth, prevColor, rcPrevViewProj, bias, skyFloor, falloff, fireflyClamp, rcJitter, width, height);
			}

			_rcResolvePass.Record(rg, in frame, cascade[0], _resources.CameraNormals, rawRc,
				d[0], s[0], dirs[0], texW[0], texH[0], Mathf.Clamp01(settings.RcCosineFloor),
				Mathf.Max(0f, settings.RcIntensity), rcJitter, width, height);
		}

		private void AllocateResources(RenderGraph rg, in FrameContext frame)
		{
			int width = frame.RenderWidth;
			int height = frame.RenderHeight;

			var colorFormat = frame.Settings.UseHdr
				? GraphicsFormat.B10G11R11_UFloatPack32
				: GraphicsFormat.R8G8B8A8_SRGB;

			bool clearColor = frame.Camera.clearFlags == CameraClearFlags.SolidColor
			                  || frame.Camera.clearFlags == CameraClearFlags.Skybox && RenderSettings.skybox == null;
			bool clearDepth = frame.Camera.clearFlags != CameraClearFlags.Nothing;
			var colorDesc = new TextureDesc(width, height)
			{
				format = colorFormat,
				clearBuffer = clearColor,
				clearColor = frame.Camera.backgroundColor.linear,
				name = "CameraColor",
			};

			var depthDesc = new TextureDesc(width, height)
			{
				depthBufferBits = DepthBits.Depth32,
				clearBuffer = clearDepth,
				name = "CameraDepth",
			};

			_resources.CameraColor = rg.CreateTexture(colorDesc);
			_resources.CameraDepth = rg.CreateTexture(depthDesc);

			// Shared world-space normal buffer, written by the depth+normal prepass (MRT color 0).
			// R8G8B8A8_SNorm stores each axis directly in [-1,1]; cleared so background texels
			// (no geometry) read as a zero normal, which consumers reject via depth.
			_resources.CameraNormals = rg.CreateTexture(new TextureDesc(width, height)
			{
				format = GraphicsFormat.R8G8B8A8_SNorm,
				clearBuffer = true,
				name = "CameraNormals",
			});

			// Transient forward+ cluster buffers, rebuilt each frame by the cull pass.
			_resources.ClusterLightGrid = rg.CreateBuffer(new BufferDesc(
				ClusterConfig.ClusterCount, sizeof(uint), GraphicsBuffer.Target.Structured)
			{
				name = "ClusterLightGrid",
			});
			_resources.ClusterLightIndices = rg.CreateBuffer(new BufferDesc(
				ClusterConfig.LightIndexCount, sizeof(uint), GraphicsBuffer.Target.Structured)
			{
				name = "ClusterLightIndices",
			});
		}

#if UNITY_EDITOR
		private void DrawGizmos(ScriptableRenderContext context, Camera camera)
		{
			if (!UnityEditor.Handles.ShouldRenderGizmos())
				return;

			context.DrawGizmos(camera, GizmoSubset.PreImageEffects);
			context.DrawGizmos(camera, GizmoSubset.PostImageEffects);
		}
#endif
	}
}
