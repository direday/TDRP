using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Debug-only fullscreen view of the environment-BRDF LUT. The LUT is a persistent RTHandle (baked
	// once, outside RenderGraph), so it exists at record time and is bound directly on the material via
	// a MaterialPropertyBlock — no global, no RG import needed. Toggled by PipelineSettings.ShowBrdfLut.
	public class BrdfLutDebugPass
	{
		private static readonly int _brdfLutTexId = Shader.PropertyToID("_BrdfLutTex");

		private readonly Material _material;
		private readonly MaterialPropertyBlock _props = new();

		public bool IsValid => _material != null;

		public BrdfLutDebugPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
			public MaterialPropertyBlock Props;
		}

		public void Record(RenderGraph rg, in FrameContext frame, RTHandle lut, TextureHandle dest)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("BRDF LUT Debug", out var passData);

			_props.SetTexture(_brdfLutTexId, lut.rt);
			passData.Material = _material;
			passData.Props = _props;

			builder.SetRenderAttachment(dest, 0);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3,
					1, data.Props);
			});
		}
	}
}
