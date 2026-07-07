using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	public class OpaqueRenderPass
	{
		private static readonly ShaderTagId[] _shaderTags =
		{
			new("TDRPUnlit"),
			new("SRPDefaultUnlit"),
		};

		private class PassData
		{
			public RendererListHandle RendererList;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle color, TextureHandle depth,
			BufferHandle clusterLightGrid, BufferHandle clusterLightIndices, TextureHandle shadowMask,
			TextureHandle ambientOcclusion, TextureHandle rcIrradiance)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("Opaque Pass", out var passData);

			var desc = new RendererListDesc(_shaderTags, frame.CullingResults, frame.Camera)
			{
				renderQueueRange = RenderQueueRange.opaque,
				sortingCriteria = SortingCriteria.CommonOpaque,
			};

			passData.RendererList = rg.CreateRendererList(desc);

			builder.UseRendererList(passData.RendererList);
			builder.SetRenderAttachment(color, 0);
			builder.SetRenderAttachmentDepth(depth, AccessFlags.Write | AccessFlags.Read);

			// The forward+ lit shader reads these via globals (bound by the cull pass).
			// RG can't see those global reads, so declare them here: this keeps the
			// transient cluster buffers alive through shading and forces the cull
			// dispatch to run before this pass.
			builder.UseBuffer(clusterLightGrid, AccessFlags.Read);
			builder.UseBuffer(clusterLightIndices, AccessFlags.Read);

			// When the main shadow was resolved to a screen-space mask, the lit shader samples
			// it via a global. Declare the read so RG keeps the mask alive and orders the
			// resolve pass before shading. Null when screen-space shadows are off (inline path).
			if (shadowMask.IsValid())
				builder.UseTexture(shadowMask, AccessFlags.Read);

			// Same story for the screen-space AO buffer: the lit shader samples it via a global
			// (under the _SCREEN_SPACE_AO keyword). Null when SSAO is off. Declaring the read
			// keeps it alive and orders the SSAO/blur passes before shading.
			if (ambientOcclusion.IsValid())
				builder.UseTexture(ambientOcclusion, AccessFlags.Read);

			// Same story for the RC diffuse irradiance: the lit shader samples it via the global
			// _RcIrradiance under the _SCREEN_SPACE_RC keyword. Null when RC is off / no history yet.
			if (rcIrradiance.IsValid())
				builder.UseTexture(rcIrradiance, AccessFlags.Read);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawRendererList(data.RendererList);
			});
		}
	}
}
