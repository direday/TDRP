using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Debug-only fullscreen overlay: colors each screen tile by its busiest cluster's
	// light count, so the (otherwise invisible) compute cull output can be eyeballed.
	// Blits `source` -> `dest` through the heatmap material; the pipeline then presents `dest`.
	public class ClusterDebugPass
	{
		private static readonly int _lightGridId = Shader.PropertyToID("_ClusterLightGrid");
		private static readonly int _screenDimensionsId = Shader.PropertyToID("_ClusterScreenDimensions");

		private readonly Material _material;

		public ClusterDebugPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public TextureHandle Source;
			public TextureHandle Dest;
			public BufferHandle LightGrid;
			public Material Material;
			public Vector4 ScreenDimensions;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle source, BufferHandle lightGrid, TextureHandle dest)
		{
			using var builder = rg.AddUnsafePass<PassData>("Cluster Debug", out var passData);

			passData.Source = source;
			passData.Dest = dest;
			passData.LightGrid = lightGrid;
			passData.Material = _material;
			// Render resolution (== display at scale 1), matching the cull pass + the render-res debug target.
			passData.ScreenDimensions = new Vector4(frame.RenderWidth, frame.RenderHeight, 0f, 0f);

			builder.UseTexture(source, AccessFlags.Read);
			builder.UseTexture(dest, AccessFlags.Write);
			builder.UseBuffer(lightGrid, AccessFlags.Read);
			builder.AllowPassCulling(false);
			builder.AllowGlobalStateModification(true); // we set globals the blit material reads

			builder.SetRenderFunc(static (PassData data, UnsafeGraphContext ctx) =>
			{
				var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
				cmd.SetGlobalBuffer(_lightGridId, data.LightGrid);
				cmd.SetGlobalVector(_screenDimensionsId, data.ScreenDimensions);
				cmd.Blit(data.Source, data.Dest, data.Material, 0);
			});
		}
	}
}
