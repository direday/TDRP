using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Produces the raw (un-denoised) screen-space ambient occlusion buffer from the depth+normal
	// prepass (R = occlusion, G = eye depth). Runs after the prepass, before shading; publishes the
	// result as the global _ScreenSpaceAO for the rest of the chain. Carries two estimators — the
	// hemisphere SSAO and GTAO — sharing all uniforms and the same output; the pipeline picks one.
	public class SsaoPass
	{
		private static readonly int _screenToWorldId = Shader.PropertyToID("_ScreenToWorld");
		private static readonly int _worldToScreenId = Shader.PropertyToID("_WorldToScreen");
		private static readonly int _aoViewId = Shader.PropertyToID("_AoView");
		private static readonly int _ssaoParamsId = Shader.PropertyToID("_SsaoParams");
		private static readonly int _ssaoSampleCountId = Shader.PropertyToID("_SsaoSampleCount");
		private static readonly int _ssaoFrameJitterId = Shader.PropertyToID("_SsaoFrameJitter");
		private static readonly int _gtaoParamsId = Shader.PropertyToID("_GtaoParams");
		private static readonly int _screenSpaceAoId = Shader.PropertyToID("_ScreenSpaceAO");

		private readonly Material _hemisphere;
		private readonly Material _gtao;

		// Hemisphere is the baseline estimator; GTAO is optional (falls back to hemisphere if its
		// shader isn't assigned).
		public bool IsValid => _hemisphere != null;

		public SsaoPass(Material hemisphere, Material gtao)
		{
			_hemisphere = hemisphere;
			_gtao = gtao;
		}

		private class PassData
		{
			public Material Material;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle depth,
			TextureHandle normals, TextureHandle ao, float frameJitter, bool useGtao)
		{
			var material = useGtao && _gtao != null ? _gtao : _hemisphere;

			// Same clip convention as the screen-space shadow pass: GPU projection with the
			// platform Y-flip baked for the offscreen RT, so depth->world reconstruction lines up.
			var proj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true);
			var view = frame.Camera.worldToCameraMatrix;
			var viewProj = proj * view;
			material.SetMatrix(_screenToWorldId, viewProj.inverse);
			material.SetMatrix(_worldToScreenId, viewProj);
			material.SetMatrix(_aoViewId, view);

			var settings = frame.Settings;
			material.SetVector(_ssaoParamsId, new Vector4(
				Mathf.Max(1e-3f, settings.SsaoRadius),
				Mathf.Max(0f, settings.SsaoIntensity),
				Mathf.Max(0f, settings.SsaoBias),
				0f));
			material.SetInt(_ssaoSampleCountId, Mathf.Max(1, settings.SsaoSampleCount));
			material.SetFloat(_ssaoFrameJitterId, frameJitter);
			// GTAO: slice/step counts + the projection focal terms (|m00|,|m11|) used to turn the
			// world radius into a screen-space march extent. Harmless to set on the hemisphere mat.
			material.SetVector(_gtaoParamsId, new Vector4(
				Mathf.Max(1, settings.GtaoSliceCount),
				Mathf.Max(1, settings.GtaoStepsPerSlice),
				Mathf.Abs(proj.m00),
				Mathf.Abs(proj.m11)));

			using var builder = rg.AddRasterRenderPass<PassData>("SSAO", out var passData);

			passData.Material = material;

			builder.SetRenderAttachment(ao, 0);
			builder.UseTexture(depth, AccessFlags.Read);
			builder.UseTexture(normals, AccessFlags.Read);

			builder.SetGlobalTextureAfterPass(ao, _screenSpaceAoId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				// Full-screen triangle (3 verts, built in the vertex shader from SV_VertexID).
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
