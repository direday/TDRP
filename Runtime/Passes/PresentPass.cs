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
	//
	// Gamma-space UI (TopDownRenderPipeline.SetGammaUiLayer): when the game registered a UI layer, the
	// same blit goes through UiGammaCompose.shader, which composites the layer over the image in gamma
	// space. Still one fullscreen pass; the cost is one extra texture fetch.
	public class PresentPass
	{
		private static readonly int _uiLayerId = Shader.PropertyToID("_UiLayer");

		private readonly Material _uiGammaMaterial;

		private class PassData
		{
			public TextureHandle Source;
			public Texture UiLayer;
			public Material UiGammaMaterial;
		}

		public PresentPass(Material uiGammaMaterial)
		{
			_uiGammaMaterial = uiGammaMaterial;
		}

		// Can this pipeline compose a gamma UI layer at all (the shader is assigned on the asset)?
		public bool SupportsGammaUi => _uiGammaMaterial != null;

		// uiLayer: the gamma UI layer to composite on this camera, or null for a plain copy.
		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle source, Texture uiLayer)
		{
			using var builder = rg.AddUnsafePass<PassData>("Present", out var passData);

			passData.Source = source;
			passData.UiLayer = SupportsGammaUi ? uiLayer : null;
			passData.UiGammaMaterial = _uiGammaMaterial;

			builder.UseTexture(source, AccessFlags.Read);
			builder.AllowPassCulling(false);

			builder.SetRenderFunc(static (PassData data, UnsafeGraphContext ctx) =>
			{
				var nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
				if (data.UiLayer != null)
				{
					// The layer is written outside the graph (UITK repaints offscreen panels before
					// cameras render), so it's bound directly rather than imported.
					data.UiGammaMaterial.SetTexture(_uiLayerId, data.UiLayer);
					nativeCmd.Blit(data.Source, BuiltinRenderTextureType.CameraTarget, data.UiGammaMaterial, 0);
				}
				else
				{
					nativeCmd.Blit(data.Source, BuiltinRenderTextureType.CameraTarget);
				}
			});
		}
	}
}
