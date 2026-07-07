using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Temporal resolve for the Radiance Cascades GI: blends the current (spatially denoised) irradiance
	// with a reprojected history, writing the accumulated result the downstream upsample + lit shader
	// read (republished as _RcIrradiance). Structurally identical to TemporalAoPass — same reproject /
	// disocclusion-reject / neighborhood-clamp kernel — but the value is RGB, so the history is RGBA16F
	// (RGB irradiance + A eye depth) instead of the scalar R16G16 the AO/shadow resolves use. Runs at
	// the RC processing resolution (half-res when RcHalfResolution), like the SSAO temporal pass.
	public class RcTemporalPass
	{
		private static readonly int _screenToWorldId = Shader.PropertyToID("_ScreenToWorld");
		private static readonly int _currViewId = Shader.PropertyToID("_CurrView");
		private static readonly int _prevViewId = Shader.PropertyToID("_PrevView");
		private static readonly int _prevViewProjId = Shader.PropertyToID("_PrevViewProj");
		private static readonly int _screenSizeId = Shader.PropertyToID("_TemporalScreenSize");
		private static readonly int _temporalParamsId = Shader.PropertyToID("_TemporalParams");
		private static readonly int _motionParamsId = Shader.PropertyToID("_TemporalMotionParams");
		private static readonly int _historyId = Shader.PropertyToID("_RcHistory");
		private static readonly int _rcIrradianceId = Shader.PropertyToID("_RcIrradiance");

		// Relative tolerance (fraction of eye depth) for the disocclusion test. Matches TemporalAoPass.
		private const float DepthRejectRelative = 0.05f;

		// Maps RcTemporalResponse (0..1) to the shader's change->alpha multiplier. At response 1, an
		// irradiance-luminance disagreement of ~1/ResponseScale already pushes the blend fully to the
		// current frame.
		private const float ResponseScale = 8f;

		private readonly Material _material;
		// Per-camera history (Scene- and Game-view cameras must not share one buffer — see
		// TemporalHistoryCache). RGBA16F: RGB = accumulated irradiance, A = eye depth for reprojection.
		private readonly TemporalHistoryCache _historyCache =
			new("RcHistory", GraphicsFormat.R16G16B16A16_SFloat);

		public bool IsValid => _material != null;

		public RcTemporalPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		// currentRc: this frame's RC irradiance (already published as the global _RcIrradiance by the
		// resolve / blur chain). Passed here only for the RG read dependency; the shader samples the
		// global. width/height are the RC buffer's dimensions (half-res when RcHalfResolution) — the
		// history and the neighborhood-clamp texel offsets must match that, not the camera.
		// motionVectors: the screen-space velocity buffer (global _CameraMotionVectorsTexture) when the
		// pipeline produced it this frame; nullHandle otherwise -> the shader falls back to camera-only
		// reprojection.
		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle currentRc,
			TextureHandle cameraDepth, TextureHandle motionVectors, float alpha, float response,
			int width, int height, out TextureHandle resolved)
		{
			var history = _historyCache.Get(frame.Camera.GetEntityId(), frame.FrameIndex);
			history.GetFrameTextures(rg, width, height, out var historyTex, out var writeTex, out var historyRt);

			var gpuProj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true);
			var view = frame.Camera.worldToCameraMatrix;
			var viewProj = gpuProj * view;

			bool useMotionVectors = motionVectors.IsValid();

			_material.SetMatrix(_screenToWorldId, viewProj.inverse);
			_material.SetMatrix(_currViewId, view);
			_material.SetMatrix(_prevViewId, history.PrevView);
			_material.SetMatrix(_prevViewProjId, history.PrevViewProj);
			_material.SetVector(_screenSizeId, new Vector4(1f / width, 1f / height, width, height));
			_material.SetVector(_temporalParamsId,
				new Vector4(alpha, DepthRejectRelative, history.HasHistory ? 1f : 0f, 1f /* neighborhood clamp on */));
			_material.SetVector(_motionParamsId,
				new Vector4(useMotionVectors ? 1f : 0f, response * ResponseScale, 0f, 0f));
			_material.SetTexture(_historyId, historyRt);

			using (var builder = rg.AddRasterRenderPass<PassData>("RC Temporal", out var passData))
			{
				passData.Material = _material;

				builder.SetRenderAttachment(writeTex, 0);
				builder.UseTexture(currentRc, AccessFlags.Read); // read as the global _RcIrradiance
				builder.UseTexture(cameraDepth, AccessFlags.Read);
				builder.UseTexture(historyTex, AccessFlags.Read);
				// Sampled via the global _CameraMotionVectorsTexture; declared for RG ordering/lifetime.
				if (useMotionVectors)
					builder.UseTexture(motionVectors, AccessFlags.Read);
				// The accumulated buffer is what the upsample / lit shader reads next.
				builder.SetGlobalTextureAfterPass(writeTex, _rcIrradianceId);

				builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
				{
					ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
				});
			}

			resolved = writeTex;

			// This frame's matrices become next frame's "prev"; flip the ping-pong buffers.
			history.Advance(view, viewProj);
		}

		public void Dispose()
		{
			_historyCache.Dispose();
		}
	}
}
