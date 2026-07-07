using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Temporal resolve: blends the current (noisy) screen-space shadow with a reprojected history
	// buffer, writing the accumulated result that the lit shader samples. Owns the persistent
	// history (TemporalHistoryCache). Reprojection follows the motion vector buffer when it's produced
	// this frame (moving receivers reproject correctly); otherwise it falls back to camera-only
	// reprojection. A shadow cast by a moving caster onto static ground still leans on the
	// neighborhood clamp — the ground (the receiver being reprojected) isn't the thing moving.
	public class TemporalShadowPass
	{
		private static readonly int _screenToWorldId = Shader.PropertyToID("_ScreenToWorld");
		private static readonly int _currViewId = Shader.PropertyToID("_CurrView");
		private static readonly int _prevViewId = Shader.PropertyToID("_PrevView");
		private static readonly int _prevViewProjId = Shader.PropertyToID("_PrevViewProj");
		private static readonly int _screenSizeId = Shader.PropertyToID("_SsShadowScreenSize");
		private static readonly int _temporalParamsId = Shader.PropertyToID("_TemporalParams");
		private static readonly int _motionParamsId = Shader.PropertyToID("_TemporalMotionParams");
		private static readonly int _historyId = Shader.PropertyToID("_SsShadowHistory");
		private static readonly int _screenShadowMaskId = Shader.PropertyToID("_ScreenShadowMask");

		// Relative tolerance (fraction of eye depth) for the disocclusion test.
		private const float DepthRejectRelative = 0.1f;

		// Maps TemporalShadowResponse (0..1) to the shader's clamp-correction->alpha multiplier. At
		// response 1, a clamp correction of ~1/ResponseScale already pushes the blend to the current
		// frame. Matches TemporalAoPass for consistency.
		private const float ResponseScale = 8f;

		private readonly Material _material;
		// Per-camera history: the Scene-view and Game-view cameras both render every editor repaint
		// and must not share one buffer (see TemporalHistoryCache).
		private readonly TemporalHistoryCache _historyCache = new("ShadowHistory");

		public bool IsValid => _material != null;

		public TemporalShadowPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		// motionVectors: the screen-space velocity buffer (published as global _CameraMotionVectorsTexture)
		// when the pipeline produced it this frame; nullHandle otherwise. When valid, reprojection
		// follows per-object motion; when null, the shader falls back to the camera-only path.
		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle rawMask,
			TextureHandle cameraDepth, TextureHandle motionVectors, float alpha, out TextureHandle resolved)
		{
			// Render resolution: the mask + history live on the render-res grid (== display at scale 1).
			int width = frame.RenderWidth;
			int height = frame.RenderHeight;

			var history = _historyCache.Get(frame.Camera.GetEntityId(), frame.FrameIndex);
			history.GetFrameTextures(rg, width, height, out var historyTex, out var writeTex, out var historyRt);

			// Current-frame matrices (GPU clip convention; renderIntoTexture bakes the RT Y-flip).
			var gpuProj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true);
			var view = frame.Camera.worldToCameraMatrix;
			var viewProj = gpuProj * view;

			bool useMotionVectors = motionVectors.IsValid();

			// All inputs set on the material (CPU-side): a raster pass can't touch global state, and
			// the persistent history RT exists now, so it binds directly (unlike transient textures,
			// which the SS/prepass passes expose as graph globals — _SsShadowRaw / _CameraDepthTexture).
			_material.SetMatrix(_screenToWorldId, viewProj.inverse);
			_material.SetMatrix(_currViewId, view);
			_material.SetMatrix(_prevViewId, history.PrevView);
			_material.SetMatrix(_prevViewProjId, history.PrevViewProj);
			_material.SetVector(_screenSizeId, new Vector4(1f / width, 1f / height, width, height));
			_material.SetVector(_temporalParamsId,
				new Vector4(alpha, DepthRejectRelative, history.HasHistory ? 1f : 0f, 1f /* neighborhood clamp on */));
			_material.SetVector(_motionParamsId,
				new Vector4(useMotionVectors ? 1f : 0f, frame.Settings.TemporalShadowResponse * ResponseScale, 0f, 0f));
			_material.SetTexture(_historyId, historyRt);

			using (var builder = rg.AddRasterRenderPass<PassData>("Temporal Shadow", out var passData))
			{
				passData.Material = _material;

				builder.SetRenderAttachment(writeTex, 0);
				builder.UseTexture(rawMask, AccessFlags.Read);
				builder.UseTexture(cameraDepth, AccessFlags.Read);
				builder.UseTexture(historyTex, AccessFlags.Read);
				// Sampled via the global _CameraMotionVectorsTexture; declared for RG ordering/lifetime.
				if (useMotionVectors)
					builder.UseTexture(motionVectors, AccessFlags.Read);
				// The resolved buffer is what the lit shader samples next.
				builder.SetGlobalTextureAfterPass(writeTex, _screenShadowMaskId);

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
