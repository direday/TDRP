using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	public class TransparentRenderPass
	{
		private static readonly ShaderTagId[] _shaderTags =
		{
			new("TDRPTransparent"),
		};

		private class PassData
		{
			public RendererListHandle RendererList;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle color, TextureHandle depth,
			BufferHandle clusterLightGrid, BufferHandle clusterLightIndices)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("Transparent Pass", out var passData);

			var desc = new RendererListDesc(_shaderTags, frame.CullingResults, frame.Camera)
			{
				// Only the transparent queue range; opaques were already drawn.
				renderQueueRange = RenderQueueRange.transparent,
				// Back-to-front: alpha blending is order-dependent, so far surfaces
				// must be drawn first and near surfaces composited over them.
				sortingCriteria = SortingCriteria.CommonTransparent,
			};

			passData.RendererList = rg.CreateRendererList(desc);

			builder.UseRendererList(passData.RendererList);
			builder.SetRenderAttachment(color, 0);
			// Read-only depth: transparents are occluded by opaque geometry (ZTest in
			// the shader) but must not write depth (ZWrite Off), so they don't clip
			// each other — their mutual ordering comes from the back-to-front sort.
			builder.SetRenderAttachmentDepth(depth, AccessFlags.Read);

			// Same forward+ lighting path as the opaque pass: the lit transparent
			// shader reads the cluster grid/indices via globals (bound by the cull
			// pass). Declaring the reads here keeps those transient buffers alive
			// through this pass and orders cull -> transparent.
			builder.UseBuffer(clusterLightGrid, AccessFlags.Read);
			builder.UseBuffer(clusterLightIndices, AccessFlags.Read);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawRendererList(data.RendererList);
			});
		}
	}
}
