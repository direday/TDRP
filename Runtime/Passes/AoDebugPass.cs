using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Debug-only fullscreen view of the raw SSAO buffer (grayscale). Reads _ScreenSpaceAO (global,
	// written by the SSAO pass) and writes a display color into `dest`; the pipeline points the
	// present pass at it. Toggled by PipelineSettings.ShowAO.
	public class AoDebugPass
	{
		private readonly Material _material;

		public bool IsValid => _material != null;

		public AoDebugPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle ao, TextureHandle dest)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("AO Debug", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(dest, 0);
			builder.UseTexture(ao, AccessFlags.Read); // sampled as the global _ScreenSpaceAO

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
