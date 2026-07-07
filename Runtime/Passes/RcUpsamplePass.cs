using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Depth-aware joint-bilateral upsample of the half-res RC irradiance (global _RcIrradiance) to a
	// full-res buffer, republished as _RcIrradiance so the lit shader LOADs it per pixel. Reads the
	// full-res depth to reconstruct tap + centre eye depths for the edge-stopping weights.
	public class RcUpsamplePass
	{
		private static readonly int _screenToWorldId = Shader.PropertyToID("_ScreenToWorld");
		private static readonly int _rcUpsampleViewId = Shader.PropertyToID("_RcUpsampleView");
		private static readonly int _rcHalfSizeId = Shader.PropertyToID("_RcHalfSize");
		private static readonly int _rcIrradianceId = Shader.PropertyToID("_RcIrradiance");

		private readonly Material _material;

		public bool IsValid => _material != null;

		public RcUpsamplePass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		// source = the half-res irradiance (bound as the global _RcIrradiance). halfW/halfH = its size.
		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle source, TextureHandle depth,
			TextureHandle normals, TextureHandle dest, int halfW, int halfH)
		{
			var proj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true);
			var view = frame.Camera.worldToCameraMatrix;
			_material.SetMatrix(_screenToWorldId, (proj * view).inverse);
			_material.SetMatrix(_rcUpsampleViewId, view);
			_material.SetVector(_rcHalfSizeId, new Vector4(halfW, halfH, 1f / halfW, 1f / halfH));

			using var builder = rg.AddRasterRenderPass<PassData>("RC Upsample", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(dest, 0);
			builder.UseTexture(source, AccessFlags.Read);  // sampled as the global _RcIrradiance
			builder.UseTexture(depth, AccessFlags.Read);   // sampled as the global _CameraDepthTexture
			builder.UseTexture(normals, AccessFlags.Read); // sampled as the global _CameraNormalsTexture
			builder.SetGlobalTextureAfterPass(dest, _rcIrradianceId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
