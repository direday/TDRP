using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// SMAA step 1 of 3: edge detection. Reads the tonemapped LDR color (_CameraColorLdr global,
	// published by TonemapPass) and writes a two-channel edge mask (R = horizontal/left edge,
	// G = vertical/top edge) from luma differences with local contrast adaptation. Non-edge pixels
	// are discarded in the shader, leaving the pre-cleared (0,0) — so the Phase-2 blend-weight pass
	// only touches actual edges. The mask is published as the global _SmaaEdgesTex for that pass.
	public class SmaaEdgePass
	{
		private static readonly int _rtMetricsId = Shader.PropertyToID("_SmaaRTMetrics");
		private static readonly int _smaaEdgesId = Shader.PropertyToID("_SmaaEdgesTex");

		private readonly Material _material;

		public bool IsValid => _material != null;

		public SmaaEdgePass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle ldrColor,
			TextureHandle normals, TextureHandle edges)
		{
			// Render resolution (== display size at RenderScale 1): SMAA runs on the render-res LDR
			// buffer, so its texel metrics must match that grid, not the display size.
			int width = frame.RenderWidth;
			int height = frame.RenderHeight;

			// (1/w, 1/h, w, h). The same value for every SMAA pass, so it lives on the shared material
			// (set CPU-side at record time, like PresentPass's keyword). Each camera records AND
			// executes before the next records, so there's no cross-camera overwrite race.
			_material.SetVector(_rtMetricsId, new Vector4(1f / width, 1f / height, width, height));

			using var builder = rg.AddRasterRenderPass<PassData>("SMAA Edges", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(edges, 0);
			builder.UseTexture(ldrColor, AccessFlags.Read); // sampled as the global _CameraColorLdr
			builder.UseTexture(normals, AccessFlags.Read);  // sampled as the global _CameraNormalsTexture (predication)
			builder.SetGlobalTextureAfterPass(edges, _smaaEdgesId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				// Pass 0 = edge detection. Full-screen triangle built from SV_VertexID.
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
