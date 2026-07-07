using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Debug-only fullscreen view of the shared world-space normal buffer (n*0.5+0.5). Reads
	// _CameraNormalsTexture (global, written by the depth+normal prepass) and writes a display
	// color into `dest`; the pipeline then points the present pass at `dest` instead of the lit
	// scene. Toggled by PipelineSettings.ShowNormals.
	public class NormalsDebugPass
	{
		private readonly Material _material;

		public bool IsValid => _material != null;

		public NormalsDebugPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle normals, TextureHandle dest)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("Normals Debug", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(dest, 0);
			// Declared so RG orders this after the prepass and keeps the buffer alive; the shader
			// samples it as the global _CameraNormalsTexture.
			builder.UseTexture(normals, AccessFlags.Read);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				// Full-screen triangle (3 verts, built in the vertex shader from SV_VertexID).
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
