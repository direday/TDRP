using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// One axis of the separable cross-bilateral AO blur. Record it twice (horizontal, then
	// vertical). Each pass reads the current _ScreenSpaceAO global (raw on the first pass, the
	// horizontally-blurred result on the second), writes `dest`, and republishes _ScreenSpaceAO
	// as `dest` so the next consumer (the other blur axis, the debug view, later the lit shader)
	// sees the latest result.
	public class SsaoBlurPass
	{
		private static readonly int _blurDirectionId = Shader.PropertyToID("_SsaoBlurDirection");
		private static readonly int _screenSpaceAoId = Shader.PropertyToID("_ScreenSpaceAO");

		private readonly Material _material;

		// One property block per axis. Both passes share the single material, so the blur
		// direction can't live on the material (the second Record would overwrite the first
		// before either executes). Per-pass property blocks, reused each frame, keep them
		// distinct without per-frame allocation.
		private readonly MaterialPropertyBlock _horizontalProps = new();
		private readonly MaterialPropertyBlock _verticalProps = new();

		public bool IsValid => _material != null;

		public SsaoBlurPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
			public MaterialPropertyBlock Properties;
		}

		// width/height are the AO buffer's dimensions (half-res when SSAO runs at half resolution),
		// so the texel step matches the buffer being blurred — not the camera.
		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle source,
			TextureHandle normals, TextureHandle dest, bool vertical, int width, int height)
		{
			// One-texel step along the blur axis, in UV space.
			var dir = vertical ? new Vector4(0f, 1f / height, 0f, 0f) : new Vector4(1f / width, 0f, 0f, 0f);

			var props = vertical ? _verticalProps : _horizontalProps;
			props.SetVector(_blurDirectionId, dir);

			using var builder = rg.AddRasterRenderPass<PassData>(
				vertical ? "SSAO Blur V" : "SSAO Blur H", out var passData);

			passData.Material = _material;
			passData.Properties = props;

			builder.SetRenderAttachment(dest, 0);
			builder.UseTexture(source, AccessFlags.Read);  // sampled as the global _ScreenSpaceAO
			builder.UseTexture(normals, AccessFlags.Read); // sampled as the global _CameraNormalsTexture
			builder.SetGlobalTextureAfterPass(dest, _screenSpaceAoId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3, 1, data.Properties);
			});
		}
	}
}
