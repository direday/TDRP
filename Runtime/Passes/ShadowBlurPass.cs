using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// One axis of the separable variance-modulated cross-bilateral blur for the screen-space shadow
	// mask. Record it twice (horizontal, then vertical). Each pass reads the current _ScreenShadowMask
	// global (the resolved mask on the first pass, the horizontally-blurred result on the second),
	// writes `dest`, and republishes _ScreenShadowMask as `dest` so the next consumer (the other axis,
	// then the lit shader) sees the latest. This is the spatial alternative to the temporal resolve —
	// it denoises few-tap PCSS without accumulating across frames, so it has no motion ghosting.
	//
	// Depth + normal for the bilateral weights come from the globals (_CameraDepthTexture /
	// _CameraNormalsTexture); the R8 mask carries no companion depth, so this pass reconstructs eye
	// depth itself (the two matrices below), which is why they're set on the material.
	public class ShadowBlurPass
	{
		private static readonly int _screenToWorldId = Shader.PropertyToID("_ScreenToWorld");
		private static readonly int _currViewId = Shader.PropertyToID("_CurrView");
		private static readonly int _blurDirectionId = Shader.PropertyToID("_ShadowBlurDirection");
		private static readonly int _blurParamsId = Shader.PropertyToID("_ShadowBlurParams");
		private static readonly int _screenShadowMaskId = Shader.PropertyToID("_ScreenShadowMask");

		private readonly Material _material;

		// One property block per axis: both passes share the single material, so the blur direction
		// can't live on the material (the second Record would overwrite the first before either runs).
		// The matrices + params are identical for both axes, so those DO go on the material.
		private readonly MaterialPropertyBlock _horizontalProps = new();
		private readonly MaterialPropertyBlock _verticalProps = new();

		public bool IsValid => _material != null;

		public ShadowBlurPass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
			public MaterialPropertyBlock Properties;
		}

		// width/height are the mask's dimensions (full res). source is the mask being blurred (bound
		// as the global _ScreenShadowMask); depth/normals are declared for RG ordering + lifetime and
		// sampled via their globals.
		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle source,
			TextureHandle depth, TextureHandle normals, TextureHandle dest, bool vertical,
			int radius, float varianceSensitivity, int width, int height)
		{
			// Eye-depth reconstruction matrices (GPU clip convention). Same for both axes -> material.
			var proj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true);
			var view = frame.Camera.worldToCameraMatrix;
			_material.SetMatrix(_screenToWorldId, (proj * view).inverse);
			_material.SetMatrix(_currViewId, view);
			_material.SetVector(_blurParamsId, new Vector4(radius, varianceSensitivity, 0f, 0f));

			// One-texel step along the blur axis, in UV space.
			var dir = vertical ? new Vector4(0f, 1f / height, 0f, 0f) : new Vector4(1f / width, 0f, 0f, 0f);
			var props = vertical ? _verticalProps : _horizontalProps;
			props.SetVector(_blurDirectionId, dir);

			using var builder = rg.AddRasterRenderPass<PassData>(
				vertical ? "Shadow Blur V" : "Shadow Blur H", out var passData);

			passData.Material = _material;
			passData.Properties = props;

			builder.SetRenderAttachment(dest, 0);
			builder.UseTexture(source, AccessFlags.Read);  // sampled as the global _ScreenShadowMask
			builder.UseTexture(depth, AccessFlags.Read);   // sampled as the global _CameraDepthTexture
			if (normals.IsValid())
				builder.UseTexture(normals, AccessFlags.Read); // sampled as the global _CameraNormalsTexture
			builder.SetGlobalTextureAfterPass(dest, _screenShadowMaskId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3, 1, data.Properties);
			});
		}
	}
}
