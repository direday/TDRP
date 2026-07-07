using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	public class SkyboxRenderPass
	{
		private class PassData
		{
			public RendererListHandle SkyboxList;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle color, TextureHandle depth)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("Skybox Pass", out var passData);

			passData.SkyboxList = rg.CreateSkyboxRendererList(frame.Camera);

			builder.UseRendererList(passData.SkyboxList);
			builder.SetRenderAttachment(color, 0);
			builder.SetRenderAttachmentDepth(depth, AccessFlags.Read);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawRendererList(data.SkyboxList);
			});
		}
	}
}
