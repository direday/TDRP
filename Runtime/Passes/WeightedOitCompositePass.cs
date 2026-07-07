using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Weighted Blended OIT — composite half. A single fullscreen triangle that resolves the
	// accumulation buffers (_OitAccum / _OitRevealage, published by the accumulate pass) and
	// blends the weighted-average transparent color over the existing CameraColor. The "over" is
	// done by the shader's fixed-function blend (Blend OneMinusSrcAlpha SrcAlpha), so this writes
	// directly into CameraColor — no extra target.
	public class WeightedOitCompositePass
	{
		private readonly Material _material;

		public bool IsValid => _material != null;

		public WeightedOitCompositePass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle color,
			TextureHandle accum, TextureHandle revealage)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("OIT Composite", out var passData);

			passData.Material = _material;

			// Blend over the opaque+skybox color already in this attachment.
			builder.SetRenderAttachment(color, 0);
			// Declared so RG keeps them alive and orders accumulate -> composite; sampled in the
			// shader via the _OitAccum / _OitRevealage globals the accumulate pass published.
			builder.UseTexture(accum, AccessFlags.Read);
			builder.UseTexture(revealage, AccessFlags.Read);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
