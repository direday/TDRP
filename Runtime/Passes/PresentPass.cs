using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Final copy of the resolved LDR image to the backbuffer (BuiltinRenderTextureType.CameraTarget).
	// Tonemapping lives in TonemapPass (which writes the sampleable CameraColorLdr); Present is a plain
	// passthrough blit, so post-tonemap LDR effects (SMAA) can be inserted before it without touching the
	// present step. An unsafe cmd.Blit to CameraTarget tolerates the target's real size — so it also does
	// the render->display upscale when RenderScale < 1 (the source is render-res, CameraTarget is display).
	// Unsafe pass: writing CameraTarget (not an RG resource) needs the native CommandBuffer escape hatch.
	public class PresentPass
	{
		private class PassData
		{
			public TextureHandle Source;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle source)
		{
			using var builder = rg.AddUnsafePass<PassData>("Present", out var passData);

			passData.Source = source;

			builder.UseTexture(source, AccessFlags.Read);
			builder.AllowPassCulling(false);

			builder.SetRenderFunc(static (PassData data, UnsafeGraphContext ctx) =>
			{
				var nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
				nativeCmd.Blit(data.Source, BuiltinRenderTextureType.CameraTarget);
			});
		}
	}
}
