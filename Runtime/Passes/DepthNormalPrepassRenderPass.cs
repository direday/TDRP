using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Depth + normal prepass. Lays down CameraDepth (early-Z for the opaque pass's ZTest Equal)
	// and, in the SAME draw via MRT, a world-space normal buffer: each opaque shader's
	// TDRPDepthOnly pass writes its interpolated world normal to color target 0.
	//
	// This replaces a separate fullscreen depth->normal reconstruction. The win is real
	// interpolated (and normal-map-ready) normals with no silhouette faceting; the cost is the
	// depth-only "double-speed Z" fast path — adding a color write forces the fragment to run.
	// Negligible for a low-overdraw top-down scene, and it's the "thin attribute buffer" the
	// forward+ architecture explicitly allows (shading still stays forward).
	//
	// Contract: every opaque shader's TDRPDepthOnly pass MUST output a world normal now, or its
	// pixels get garbage normals in the buffer.
	public class DepthNormalPrepassRenderPass
	{
		private static readonly int _cameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
		private static readonly int _cameraNormalsTextureId = Shader.PropertyToID("_CameraNormalsTexture");

		private static readonly ShaderTagId[] _shaderTags =
		{
			new("TDRPDepthOnly"),
		};

		private class PassData
		{
			public RendererListHandle RendererList;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle depth, TextureHandle normals)
		{
			using var builder = rg.AddRasterRenderPass<PassData>("Depth+Normal Prepass", out var passData);

			var desc = new RendererListDesc(_shaderTags, frame.CullingResults, frame.Camera)
			{
				renderQueueRange = RenderQueueRange.opaque,
				sortingCriteria = SortingCriteria.CommonOpaque,
			};

			passData.RendererList = rg.CreateRendererList(desc);

			builder.UseRendererList(passData.RendererList);
			builder.SetRenderAttachment(normals, 0);
			builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);

			// Expose both as globals for later passes: depth for world-pos reconstruction (SS
			// shadows / SSAO), normals as the shared scene-normal substrate sampled via Normals.hlsl.
			builder.SetGlobalTextureAfterPass(depth, _cameraDepthTextureId);
			builder.SetGlobalTextureAfterPass(normals, _cameraNormalsTextureId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawRendererList(data.RendererList);
			});
		}
	}
}
