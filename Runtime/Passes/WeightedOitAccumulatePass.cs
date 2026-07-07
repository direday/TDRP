using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Weighted Blended OIT — accumulation half. Draws every transparent material's
	// TDRPTransparentOit pass into two MRT buffers (no sort): color 0 = weighted-sum accum
	// (RGBA16F), color 1 = revealage (R8, the running (1-alpha) product). The composite pass
	// resolves them. The two buffers are published as globals (_OitAccum / _OitRevealage) so the
	// composite reads them the same way other screen-space passes consume their producers.
	public class WeightedOitAccumulatePass
	{
		private static readonly ShaderTagId[] _shaderTags =
		{
			new("TDRPTransparentOit"),
		};

		private static readonly int _accumId = Shader.PropertyToID("_OitAccum");
		private static readonly int _revealageId = Shader.PropertyToID("_OitRevealage");

		private class PassData
		{
			public RendererListHandle RendererList;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle accum,
			TextureHandle revealage, TextureHandle depth,
			BufferHandle clusterLightGrid, BufferHandle clusterLightIndices)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("OIT Accumulate", out var passData);

			var desc = new RendererListDesc(_shaderTags, frame.CullingResults, frame.Camera)
			{
				renderQueueRange = RenderQueueRange.transparent,
				// Order is irrelevant to the result (that's the whole point), so don't pay for a
				// sort — front-to-back is marginally better for early-Z against the opaque depth.
				sortingCriteria = SortingCriteria.CommonOpaque,
			};

			passData.RendererList = rg.CreateRendererList(desc);

			builder.UseRendererList(passData.RendererList);
			// MRT: accum on slot 0, revealage on slot 1. Per-attachment blend states live in the
			// shader pass (Blend 0 One One / Blend 1 Zero OneMinusSrcColor).
			builder.SetRenderAttachment(accum, 0);
			builder.SetRenderAttachment(revealage, 1);
			// Read-only depth: transparents are occluded by opaques but don't write/clip depth.
			builder.SetRenderAttachmentDepth(depth, AccessFlags.Read);

			// Forward+ lighting globals, same as the opaque/sorted-transparent passes.
			builder.UseBuffer(clusterLightGrid, AccessFlags.Read);
			builder.UseBuffer(clusterLightIndices, AccessFlags.Read);

			// Publish both buffers as globals so the composite pass can sample them. RG sees the
			// dependency and orders accumulate -> composite, keeping the textures alive between.
			builder.SetGlobalTextureAfterPass(accum, _accumId);
			builder.SetGlobalTextureAfterPass(revealage, _revealageId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawRendererList(data.RendererList);
			});
		}
	}
}
