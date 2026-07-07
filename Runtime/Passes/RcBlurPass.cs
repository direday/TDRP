using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// One axis of the separable depth/normal cross-bilateral blur that denoises the Radiance Cascades
	// irradiance. Record it twice (horizontal, then vertical). Each pass reads the current _RcIrradiance
	// global (the raw gather on the first pass, the H-blurred result on the second), writes `dest`, and
	// republishes _RcIrradiance = dest so the next consumer (the other axis, then the lit shader) sees
	// the latest — same republish pattern as ShadowBlurPass / SsaoBlurPass.
	//
	// Depth + normal for the bilateral weights come from the globals; the HDR irradiance carries no
	// companion depth, so this reconstructs eye depth itself (the two matrices below on the material).
	public class RcBlurPass
	{
		private static readonly int _screenToWorldId = Shader.PropertyToID("_ScreenToWorld");
		private static readonly int _rcBlurViewId = Shader.PropertyToID("_RcBlurView");
		private static readonly int _blurDirectionId = Shader.PropertyToID("_RcBlurDirection");
		private static readonly int _blurParamsId = Shader.PropertyToID("_RcBlurParams");
		private static readonly int _rcIrradianceId = Shader.PropertyToID("_RcIrradiance");

		private readonly Material _material;

		// One property block per axis: both passes share the single material, so the blur direction
		// can't live on the material (the second Record would overwrite the first before either runs).
		// The matrices + params are identical for both axes, so those DO go on the material.
		private readonly MaterialPropertyBlock _horizontalProps = new();
		private readonly MaterialPropertyBlock _verticalProps = new();

		public bool IsValid => _material != null;

		public RcBlurPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
			public MaterialPropertyBlock Properties;
		}

		// source is the irradiance being blurred (bound as the global _RcIrradiance); depth/normals are
		// declared for RG ordering + lifetime and sampled via their globals. width/height = full res.
		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle source,
			TextureHandle depth, TextureHandle normals, TextureHandle dest, bool vertical,
			int radius, int width, int height)
		{
			// Eye-depth reconstruction matrices (GPU clip convention). Same for both axes -> material.
			var proj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true);
			var view = frame.Camera.worldToCameraMatrix;
			_material.SetMatrix(_screenToWorldId, (proj * view).inverse);
			_material.SetMatrix(_rcBlurViewId, view);
			_material.SetVector(_blurParamsId, new Vector4(radius, 0f, 0f, 0f));

			// One-texel step along the blur axis, in UV space.
			var dir = vertical ? new Vector4(0f, 1f / height, 0f, 0f) : new Vector4(1f / width, 0f, 0f, 0f);
			var props = vertical ? _verticalProps : _horizontalProps;
			props.SetVector(_blurDirectionId, dir);

			using var builder = rg.AddRasterRenderPass<PassData>(
				vertical ? "RC Blur V" : "RC Blur H", out var passData);

			passData.Material = _material;
			passData.Properties = props;

			builder.SetRenderAttachment(dest, 0);
			builder.UseTexture(source, AccessFlags.Read); // sampled as the global _RcIrradiance
			builder.UseTexture(depth, AccessFlags.Read);  // sampled as the global _CameraDepthTexture
			if (normals.IsValid())
				builder.UseTexture(normals, AccessFlags.Read); // sampled as the global _CameraNormalsTexture
			builder.SetGlobalTextureAfterPass(dest, _rcIrradianceId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3, 1, data.Properties);
			});
		}
	}
}
