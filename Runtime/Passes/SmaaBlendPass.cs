using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// SMAA step 3 of 3: neighborhood blending. For each pixel, fetches its own + neighbours' blend
	// weights (_SmaaBlendTex, from the weight pass) and does a single bilinear-style blend of the
	// tonemapped color (_CameraColorLdr) toward the appropriate neighbour by that weight -> the final
	// anti-aliased LDR image, written to `dest` (the buffer Present blits to the backbuffer).
	public class SmaaBlendPass
	{
		private static readonly int _rtMetricsId = Shader.PropertyToID("_SmaaRTMetrics");

		private readonly Material _material;

		public bool IsValid => _material != null;

		public SmaaBlendPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle ldrColor,
			TextureHandle weights, TextureHandle dest)
		{
			// Render resolution (== display size at RenderScale 1): the metrics describe the render-res
			// inputs (_CameraColorLdr + weights) this pass samples. When render scale < 1 the destination
			// is the larger display-res backbuffer, so this pass also upscales — the neighbor offsets stay
			// in render-res texels (matching the sampled source), which is what these metrics encode.
			int width = frame.RenderWidth;
			int height = frame.RenderHeight;
			_material.SetVector(_rtMetricsId, new Vector4(1f / width, 1f / height, width, height));

			using var builder = rg.AddRasterRenderPass<PassData>("SMAA Neighborhood Blend", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(dest, 0);
			builder.UseTexture(ldrColor, AccessFlags.Read); // sampled as the global _CameraColorLdr
			builder.UseTexture(weights, AccessFlags.Read);  // sampled as the global _SmaaBlendTex

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				// Pass 2 = neighborhood blend.
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 2, MeshTopology.Triangles, 3);
			});
		}
	}
}
