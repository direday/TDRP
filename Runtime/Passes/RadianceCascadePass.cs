using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Screen-space Radiance Cascades gather (Milestone 1: single cascade). Runs after the depth+normal
	// prepass and before opaque shading: it marches this frame's depth but samples RADIANCE from the
	// PREVIOUS frame's HDR color (bound as _RcPrevColor), producing a full-res diffuse irradiance buffer
	// the lit shader consumes as the global _RcIrradiance (under the _SCREEN_SPACE_RC keyword).
	//
	// Matrix setup mirrors SsaoPass exactly (the depth->world / world->screen reconstruction is the same
	// machinery). The prev-color history is a persistent RTHandle, so it's bound straight onto the
	// material at record time (like SSAO binds its matrices) — no UseTexture needed, since it's last
	// frame's resource, not a graph texture produced this frame.
	public class RadianceCascadePass
	{
		private static readonly int _screenToWorldId = Shader.PropertyToID("_ScreenToWorld");
		private static readonly int _worldToScreenId = Shader.PropertyToID("_WorldToScreen");
		private static readonly int _rcViewId = Shader.PropertyToID("_RcView");
		private static readonly int _rcPrevColorId = Shader.PropertyToID("_RcPrevColor");
		private static readonly int _rcParamsId = Shader.PropertyToID("_RcParams");
		private static readonly int _rcParams2Id = Shader.PropertyToID("_RcParams2");
		private static readonly int _rcIrradianceId = Shader.PropertyToID("_RcIrradiance");

		private readonly Material _material;

		public bool IsValid => _material != null;

		public RadianceCascadePass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		// prevColor: last frame's HDR color (persistent RTHandle). frameJitter: [0,1) rotation offset
		// for the ray fan (0 in M1 — no temporal accumulation yet, but plumbed for later).
		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle depth,
			TextureHandle normals, RTHandle prevColor, TextureHandle irradiance, float frameJitter)
		{
			// Same clip convention as SSAO / the screen-space passes: GPU projection with the platform
			// Y-flip baked for the offscreen RT, so depth->world reconstruction and re-projection align.
			var proj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true);
			var view = frame.Camera.worldToCameraMatrix;
			var viewProj = proj * view;
			_material.SetMatrix(_screenToWorldId, viewProj.inverse);
			_material.SetMatrix(_worldToScreenId, viewProj);
			_material.SetMatrix(_rcViewId, view);

			// Previous frame's color, bound directly (persistent RT exists now). SetTexture uses the
			// RTHandle's implicit RenderTexture conversion.
			_material.SetTexture(_rcPrevColorId, prevColor);

			var settings = frame.Settings;
			_material.SetVector(_rcParamsId, new Vector4(
				Mathf.Max(1, settings.RcRayCount),
				Mathf.Max(1, settings.RcStepCount),
				Mathf.Max(1e-3f, settings.RcMaxDistance),
				Mathf.Max(0f, settings.RcIntensity)));
			_material.SetVector(_rcParams2Id, new Vector4(
				Mathf.Max(0f, settings.RcDepthBias),
				Mathf.Clamp01(settings.RcCosineFloor),
				frameJitter,
				Mathf.Clamp01(settings.RcSkyVisibility)));

			using var builder = rg.AddRasterRenderPass<PassData>("Radiance Cascades", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(irradiance, 0);
			builder.UseTexture(depth, AccessFlags.Read);
			builder.UseTexture(normals, AccessFlags.Read);

			builder.SetGlobalTextureAfterPass(irradiance, _rcIrradianceId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
