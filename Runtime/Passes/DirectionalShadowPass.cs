using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	public class DirectionalShadowPass
	{
		private static readonly int _shadowmapId = Shader.PropertyToID("_MainLightShadowmap");

		private class PassData
		{
			public RendererListHandle ShadowCasters;
			public Matrix4x4 View;
			public Matrix4x4 Proj;
			public Matrix4x4 CameraView;
			public Matrix4x4 CameraProj;
		}

		public void Record(RenderGraph rg, in FrameContext frame, in MainLightData mainLight, TextureHandle shadowMap)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("Directional Shadow", out var passData);

			// ShadowDrawingSettings selects the "ShadowCaster" pass of each renderer for this
			// light. We don't set splitData (deprecated — frustum culling of casters now goes
			// through ScriptableRenderContext.CullShadowCasters); without it the list includes
			// all shadow casters, which our ortho clips anyway. Fine for the current scale.
			var shadowSettings = new ShadowDrawingSettings(frame.CullingResults, mainLight.Index);
			passData.ShadowCasters = rg.CreateShadowRendererList(ref shadowSettings);
			passData.View = mainLight.ShadowView;
			passData.Proj = mainLight.ShadowProj;
			// Captured so we can restore them after rendering the shadow map.
			passData.CameraView = frame.Camera.worldToCameraMatrix;
			passData.CameraProj = frame.Camera.projectionMatrix;

			builder.UseRendererList(passData.ShadowCasters);
			// Depth-only target: the rendered depth IS the shadow map.
			builder.SetRenderAttachmentDepth(shadowMap, AccessFlags.Write);
			// Publish the map as a global texture for the lit shaders. This also creates
			// the dependency so later passes that read it run after this one.
			builder.SetGlobalTextureAfterPass(shadowMap, _shadowmapId);
			// The pass's only consumer reads it via a global, which RG can't fully see;
			// keep it from being culled.
			builder.AllowPassCulling(false);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				// Swap the camera's matrices for the light's-eye matrices so casters
				// render their depth from the light's point of view.
				ctx.cmd.SetViewProjectionMatrices(data.View, data.Proj);
				ctx.cmd.DrawRendererList(data.ShadowCasters);

				// Restore the camera matrices. RenderGraph does NOT reset matrix state
				// between passes, so without this the depth prepass / opaque / skybox
				// would inherit the light's-eye matrices and render the whole scene from
				// the light's point of view.
				ctx.cmd.SetViewProjectionMatrices(data.CameraView, data.CameraProj);
			});
		}
	}
}
