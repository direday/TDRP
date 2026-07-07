using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Debug-only fullscreen view of the motion vector buffer (flow map: direction -> hue, magnitude
	// -> brightness). Reads _CameraMotionVectorsTexture (global, written by MotionVectorPass) and
	// writes a display color into `dest`; the pipeline then presents `dest` instead of the lit scene.
	// Toggled by PipelineSettings.ShowMotionVectors.
	public class MotionVectorsDebugPass
	{
		private readonly Material _material;

		public bool IsValid => _material != null;

		public MotionVectorsDebugPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle motionVectors, TextureHandle dest)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("Motion Vectors Debug", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(dest, 0);
			// Declared so RG orders this after the motion vector pass and keeps the buffer alive;
			// the shader samples it as the global _CameraMotionVectorsTexture.
			builder.UseTexture(motionVectors, AccessFlags.Read);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
