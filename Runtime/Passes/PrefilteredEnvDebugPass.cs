using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Debug-only fullscreen view of the prefiltered environment cube (lat-long unwrap). The cube is a
	// persistent RTHandle, bound directly on the material via a MaterialPropertyBlock. The mip to show
	// is derived from ShowPrefilteredEnvRoughness. Toggled by PipelineSettings.ShowPrefilteredEnv.
	public class PrefilteredEnvDebugPass
	{
		private static readonly int _prefilteredEnvTexId = Shader.PropertyToID("_PrefilteredEnvTex");
		private static readonly int _paramsId = Shader.PropertyToID("_PrefilteredEnvDebugParams");

		private readonly Material _material;
		private readonly MaterialPropertyBlock _props = new();

		public bool IsValid => _material != null;

		public PrefilteredEnvDebugPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
			public MaterialPropertyBlock Props;
		}

		public void Record(RenderGraph rg, in FrameContext frame, RTHandle cube, float mip, TextureHandle dest)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("Prefiltered Env Debug", out var passData);

			_props.SetTexture(_prefilteredEnvTexId, cube.rt);
			_props.SetVector(_paramsId, new Vector4(mip, 0f, 0f, 0f));
			passData.Material = _material;
			passData.Props = _props;

			builder.SetRenderAttachment(dest, 0);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3,
					1, data.Props);
			});
		}
	}
}
