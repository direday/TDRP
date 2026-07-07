using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// SMAA step 2 of 3: blend weight calculation. Reads the edge mask (_SmaaEdgesTex global, from the
	// edge pass) and the two precomputed LUTs (AreaTex, SearchTex), classifies each edge pattern by
	// searching along the edge for its extent, and looks up the coverage area -> RGBA weights. The
	// weights tell the final blend how much of each neighbour to mix in. Published as _SmaaBlendTex.
	public class SmaaBlendWeightPass
	{
		private static readonly int _rtMetricsId = Shader.PropertyToID("_SmaaRTMetrics");
		private static readonly int _areaTexId = Shader.PropertyToID("_SmaaAreaTex");
		private static readonly int _searchTexId = Shader.PropertyToID("_SmaaSearchTex");
		private static readonly int _blendTexId = Shader.PropertyToID("_SmaaBlendTex");

		private readonly Material _material;
		private readonly Texture2D _areaTex;
		private readonly Texture2D _searchTex;

		// The LUTs are mandatory — without them the search/area lookups read garbage.
		public bool IsValid => _material != null && _areaTex != null && _searchTex != null;

		public SmaaBlendWeightPass(Material material, Texture2D areaTex, Texture2D searchTex)
		{
			_material = material;
			_areaTex = areaTex;
			_searchTex = searchTex;
		}

		private class PassData
		{
			public Material Material;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle edges, TextureHandle weights)
		{
			// Render resolution (== display size at RenderScale 1): the edge/weight buffers are render-res.
			int width = frame.RenderWidth;
			int height = frame.RenderHeight;

			// RT metrics + the persistent LUTs live on the shared material (record-time CPU state,
			// same value across the SMAA passes; each camera records+executes before the next).
			_material.SetVector(_rtMetricsId, new Vector4(1f / width, 1f / height, width, height));
			_material.SetTexture(_areaTexId, _areaTex);
			_material.SetTexture(_searchTexId, _searchTex);

			using var builder = rg.AddRasterRenderPass<PassData>("SMAA Blend Weights", out var passData);

			passData.Material = _material;

			builder.SetRenderAttachment(weights, 0);
			builder.UseTexture(edges, AccessFlags.Read); // sampled as the global _SmaaEdgesTex
			builder.SetGlobalTextureAfterPass(weights, _blendTexId);

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				// Pass 1 = blend weight calculation.
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 1, MeshTopology.Triangles, 3);
			});
		}
	}
}
