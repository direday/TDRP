using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Resolves the main directional shadow into a full-screen mask (one fragment per pixel
	// runs PCSS), which the lit shader then samples instead of computing the shadow inline.
	// This is the structural prerequisite for temporal accumulation: the shadow now lives in
	// a screen-space buffer that a later pass can reproject + blend across frames.
	//
	// Inputs are all bound as globals already: the shadow map + min/max pyramid by the shadow
	// passes, the camera depth by the depth prepass, the light direction + shadow matrices by
	// SetupLightingPass. We only declare them as RG reads here (for lifetime + ordering) and
	// add the one matrix that's specific to this pass (screen -> world).
	public class ScreenSpaceShadowPass
	{
		private static readonly int _screenToWorldId = Shader.PropertyToID("_ScreenToWorld");
		private static readonly int _shadowJitterId = Shader.PropertyToID("_ShadowRotationJitter");
		private static readonly int _pcssTapCountId = Shader.PropertyToID("_PcssTapCount");
		private static readonly int _screenShadowMaskId = Shader.PropertyToID("_ScreenShadowMask");
		private static readonly int _ssShadowRawId = Shader.PropertyToID("_SsShadowRaw");

		private readonly Material _material;

		public bool IsValid => _material != null;

		public ScreenSpaceShadowPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		// forTemporal: when true, this mask is an intermediate consumed by the temporal resolve, so
		// it's exposed as _SsShadowRaw (not the final _ScreenShadowMask the lit shader samples), and
		// the sample disk is rotated by rotationJitter so each frame produces decorrelated noise.
		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle depth,
			TextureHandle normals, TextureHandle shadowMap, TextureHandle shadowMinMax, TextureHandle mask,
			bool forTemporal, float rotationJitter, int tapCount)
		{
			// Inverse view-projection in the GPU clip convention. renderIntoTexture: true bakes
			// the platform Y-flip for the offscreen RT we render into, so reconstructing world
			// pos from (uv, deviceDepth) lines up with how the depth was rasterised. Set on the
			// material (CPU-side, at record time) rather than as a render-graph global — a raster
			// pass can't modify global state without opting in, and this value is only used here.
			var proj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true);
			var view = frame.Camera.worldToCameraMatrix;
			_material.SetMatrix(_screenToWorldId, (proj * view).inverse);

			_material.SetFloat(_shadowJitterId, rotationJitter);
			_material.SetInt(_pcssTapCountId, tapCount);

			using var builder = rg.AddRasterRenderPass<PassData>("Screen-Space Shadows", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(mask, 0);
			builder.UseTexture(depth, AccessFlags.Read);
			// Sampled for normal bias (global _CameraNormalsTexture). Optional input, like the
			// min/max pyramid below: if the CameraNormals pass didn't run (no shader assigned),
			// the handle is null — skip the read rather than feed UseTexture an invalid handle.
			if (normals.IsValid())
				builder.UseTexture(normals, AccessFlags.Read);
			builder.UseTexture(shadowMap, AccessFlags.Read);
			if (shadowMinMax.IsValid())
				builder.UseTexture(shadowMinMax, AccessFlags.Read);

			// Expose the mask as a global for the next consumer: the temporal resolve (_SsShadowRaw)
			// when accumulating, otherwise straight to the lit shader (_ScreenShadowMask).
			builder.SetGlobalTextureAfterPass(mask, forTemporal ? _ssShadowRawId : _screenShadowMaskId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				// Full-screen triangle (3 verts, built in the vertex shader from SV_VertexID).
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
