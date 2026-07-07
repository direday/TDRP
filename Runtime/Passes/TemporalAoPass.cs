using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Temporal resolve for SSAO: blends the current (noisy, jittered) AO with a reprojected history
	// buffer, writing the accumulated result the lit shader samples (republished as _ScreenSpaceAO).
	// Owns its persistent history (a shared TemporalHistory). Same kernel as the shadow temporal
	// resolve — reprojects via the motion vector buffer when produced this frame (no ghosting on
	// movers), else falls back to camera-only reprojection; the neighborhood clamp guards both.
	public class TemporalAoPass
	{
		private static readonly int _screenToWorldId = Shader.PropertyToID("_ScreenToWorld");
		private static readonly int _currViewId = Shader.PropertyToID("_CurrView");
		private static readonly int _prevViewId = Shader.PropertyToID("_PrevView");
		private static readonly int _prevViewProjId = Shader.PropertyToID("_PrevViewProj");
		private static readonly int _screenSizeId = Shader.PropertyToID("_TemporalScreenSize");
		private static readonly int _temporalParamsId = Shader.PropertyToID("_TemporalParams");
		private static readonly int _motionParamsId = Shader.PropertyToID("_TemporalMotionParams");
		private static readonly int _historyId = Shader.PropertyToID("_AoHistory");
		private static readonly int _screenSpaceAoId = Shader.PropertyToID("_ScreenSpaceAO");

		// Relative tolerance (fraction of eye depth) for the disocclusion test.
		private const float DepthRejectRelative = 0.1f;

		// Maps SsaoTemporalResponse (0..1) to the shader's change->alpha multiplier. At response 1,
		// an AO disagreement of ~1/ResponseScale already pushes the blend fully to the current frame.
		private const float ResponseScale = 8f;

		private readonly Material _material;
		// Per-camera history: the Scene-view and Game-view cameras both render every editor repaint
		// and must not share one buffer (see TemporalHistoryCache).
		private readonly TemporalHistoryCache _historyCache = new("AoHistory");

		public bool IsValid => _material != null;

		public TemporalAoPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		// currentAo: this frame's AO (already published as the global _ScreenSpaceAO by the SSAO /
		// blur chain). Passed here only for the RG read dependency; the shader samples the global.
		// width/height are the AO buffer's dimensions (half-res when SSAO is half-res); the history
		// and the neighborhood-clamp texel offsets must match that, not the camera.
		// motionVectors: the screen-space velocity buffer (published as global _CameraMotionVectorsTexture)
		// when the pipeline produced it this frame; nullHandle otherwise. When valid, reprojection
		// follows per-object motion (no ghosting on movers); when null, the shader falls back to the
		// camera-only path.
		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle currentAo,
			TextureHandle cameraDepth, TextureHandle motionVectors, float alpha, int width, int height,
			out TextureHandle resolved)
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
				new Vector4(useMotionVectors ? 1f : 0f, frame.Settings.SsaoTemporalResponse * ResponseScale, 0f, 0f));
			_material.SetTexture(_historyId, historyRt);

			using (var builder = rg.AddRasterRenderPass<PassData>("Temporal AO", out var passData))
			{
				passData.Material = _material;

				builder.SetRenderAttachment(writeTex, 0);
				builder.UseTexture(currentAo, AccessFlags.Read);
				builder.UseTexture(cameraDepth, AccessFlags.Read);
				builder.UseTexture(historyTex, AccessFlags.Read);
				// Sampled via the global _CameraMotionVectorsTexture; declared for RG ordering/lifetime.
				if (useMotionVectors)
					builder.UseTexture(motionVectors, AccessFlags.Read);
				// The resolved buffer is what the lit shader samples next.
				builder.SetGlobalTextureAfterPass(writeTex, _screenSpaceAoId);

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
