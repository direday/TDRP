using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Resolves the merged cascade 0 (global _RcCascade0, published by the cascade-0 build) into the
	// full-res per-pixel diffuse irradiance _RcIrradiance — the same buffer the M1 gather produced, so
	// the downstream blur + apply are identical. Reads the scene normals (global) for the cosine
	// integration; ambient comes from the globals SetupLightingPass binds.
	public class RcResolvePass
	{
		private static readonly int _rcViewId = Shader.PropertyToID("_RcView");
		private static readonly int _rcScreenSizeId = Shader.PropertyToID("_RcScreenSize");
		private static readonly int _rcResolveParamsId = Shader.PropertyToID("_RcResolveParams");
		private static readonly int _rcResolveParams2Id = Shader.PropertyToID("_RcResolveParams2");
		private static readonly int _rcJitterId = Shader.PropertyToID("_RcJitter");
		private static readonly int _rcIrradianceId = Shader.PropertyToID("_RcIrradiance");

		private readonly Material _material;

		public bool IsValid => _material != null;

		public RcResolvePass(Material material)
		{
			_material = material;
		}

		private class PassData
		{
			public Material Material;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle cascade0, TextureHandle normals,
			TextureHandle irradiance, int blockSide, float spacing, int directions, int cascade0TexW,
			int cascade0TexH, float cosineFloor, float intensity, Vector2 jitter, int screenW, int screenH)
		{
			_material.SetMatrix(_rcViewId, frame.Camera.worldToCameraMatrix);
			_material.SetVector(_rcScreenSizeId, new Vector4(screenW, screenH, 1f / screenW, 1f / screenH));
			_material.SetVector(_rcResolveParamsId, new Vector4(blockSide, spacing, directions, cascade0TexW));
			_material.SetVector(_rcResolveParams2Id, new Vector4(cascade0TexH, cosineFloor, intensity, 0f));
			_material.SetVector(_rcJitterId, jitter); // must match the build's per-frame probe offset

			using var builder = rg.AddRasterRenderPass<PassData>("RC Resolve", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(irradiance, 0);
			builder.UseTexture(cascade0, AccessFlags.Read); // read as the global _RcCascade0
			builder.UseTexture(normals, AccessFlags.Read);  // read as the global _CameraNormalsTexture
			builder.SetGlobalTextureAfterPass(irradiance, _rcIrradianceId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
			});
		}
	}
}
