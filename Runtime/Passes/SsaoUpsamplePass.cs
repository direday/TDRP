using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Joint bilateral upsample of the half-res AO buffer to full resolution. Reads the half-res
	// AO (via the global _ScreenSpaceAO) + the full-res depth, writes the full-res result and
	// republishes it as _ScreenSpaceAO for the lit shader. Only runs when SSAO is half-res.
	public class SsaoUpsamplePass
	{
		private static readonly int _screenToWorldId = Shader.PropertyToID("_ScreenToWorld");
		private static readonly int _aoViewId = Shader.PropertyToID("_AoView");
		private static readonly int _halfTexelSizeId = Shader.PropertyToID("_SsaoHalfTexelSize");
		private static readonly int _screenSpaceAoId = Shader.PropertyToID("_ScreenSpaceAO");

		private readonly Material _material;

		public bool IsValid => _material != null;

		public SsaoUpsamplePass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		// halfWidth/halfHeight: the source (half-res) AO dimensions. dest is full-res.
		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle halfResAo,
			TextureHandle depth, TextureHandle normals, TextureHandle dest, int halfWidth, int halfHeight)
		{
			var proj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true);
			var view = frame.Camera.worldToCameraMatrix;
			_material.SetMatrix(_screenToWorldId, (proj * view).inverse);
			_material.SetMatrix(_aoViewId, view);
			_material.SetVector(_halfTexelSizeId,
				new Vector4(1f / halfWidth, 1f / halfHeight, halfWidth, halfHeight));

			using var builder = rg.AddRasterRenderPass<PassData>("SSAO Upsample", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(dest, 0);
			builder.UseTexture(halfResAo, AccessFlags.Read); // sampled as the global _ScreenSpaceAO
			builder.UseTexture(depth, AccessFlags.Read);
			builder.UseTexture(normals, AccessFlags.Read); // sampled as the global _CameraNormalsTexture
			builder.SetGlobalTextureAfterPass(dest, _screenSpaceAoId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
