using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Debug-only fullscreen view of the raw Radiance Cascades irradiance buffer (_RcIrradiance, global,
	// written by the RC gather pass). Writes the HDR irradiance into `dest` so the pipeline can route it
	// through tonemap like the scene color. Toggled by PipelineSettings.ShowRcRadiance.
	public class RcDebugPass
	{
		private readonly Material _material;

		public bool IsValid => _material != null;

		public RcDebugPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle irradiance, TextureHandle dest)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("RC Debug", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(dest, 0);
			builder.UseTexture(irradiance, AccessFlags.Read); // sampled as the global _RcIrradiance

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
