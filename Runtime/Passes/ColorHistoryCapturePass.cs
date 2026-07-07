using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Captures this frame's HDR scene color (CameraColor, after opaque+skybox but BEFORE transparents,
	// and BEFORE tonemap — we want linear radiance, not the clamped LDR image) into the persistent color
	// history, so NEXT frame's Radiance Cascades gather can sample it as incoming radiance.
	//
	// The pipeline records this BEFORE the transparent passes on purpose: screen-space GI wants an
	// opaque-only source (as URP/HDRP SSGI/SSR do). Transparents — especially bright unlit / additive
	// particles — have no reliable depth/normal to act as emitters, and feeding them back as radiance
	// produces halos and firefly noise through the cascades. The transparent passes write a later
	// version of CameraColor, which RenderGraph orders after this read, so the final image is unaffected.
	//
	// Unsafe pass (a raw CommandBuffer.CopyTexture escape hatch, like TonemapPass/PresentPass reach for
	// a native cmd): the source is a TRANSIENT graph texture and the destination is an IMPORTED
	// persistent RTHandle. CopyTexture is a straight texel copy — no shader, no scaling, and crucially
	// no Y-flip ambiguity — so the history holds pixel-for-pixel what CameraColor holds. That requires
	// identical size AND format, which is why ColorHistory is allocated to match CameraColor's format.
	public class ColorHistoryCapturePass
	{
		private class PassData
		{
			public TextureHandle Source;
			public TextureHandle Dest;
		}

		public void Record(RenderGraph rg, TextureHandle cameraColor, TextureHandle historyWrite)
		{
			using var builder = rg.AddUnsafePass<PassData>("Color History Capture", out var passData);

			passData.Source = cameraColor;
			passData.Dest = historyWrite;

			builder.UseTexture(cameraColor, AccessFlags.Read);
			builder.UseTexture(historyWrite, AccessFlags.Write);
			// It has a side effect RG can't see (writing a persistent resource consumed next frame), so
			// it must not be culled and must run after the scene color is complete.
			builder.AllowPassCulling(false);

			builder.SetRenderFunc(static (PassData data, UnsafeGraphContext ctx) =>
			{
				var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
				cmd.CopyTexture(data.Source, data.Dest);
			});
		}
	}
}
