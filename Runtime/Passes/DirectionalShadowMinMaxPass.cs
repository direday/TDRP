using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Builds the coarse min/max-depth level over the shadow map and publishes it as a global
	// (_MainLightShadowMinMax) for the PCSS blocker search to early-out on. This is a pure
	// acceleration layer: it only short-circuits search windows whose result is already
	// determined (wholly lit or wholly shadowed), so the shaded image is byte-identical with
	// or without it — which is exactly how we verify it.
	public class DirectionalShadowMinMaxPass
	{
		private static readonly int _shadowDepthId = Shader.PropertyToID("_ShadowDepth");
		private static readonly int _resultId = Shader.PropertyToID("_ShadowMinMaxResult");
		private static readonly int _dimsId = Shader.PropertyToID("_ShadowMinMaxDims");
		private static readonly int _globalId = Shader.PropertyToID("_MainLightShadowMinMax");

		private readonly ComputeShader _cs;
		private readonly int _kernel;

		public bool IsValid => _cs != null && _kernel >= 0;

		public DirectionalShadowMinMaxPass(ComputeShader cs)
		{
			_cs = cs;
			_kernel = cs != null ? cs.FindKernel("ShadowMinMaxReduce") : -1;
		}

		private class PassData
		{
			public ComputeShader Cs;
			public int Kernel;
			public TextureHandle ShadowMap;
			public TextureHandle MinMax;
			public Vector4 Dims;
			public int Groups;
		}

		public void Record(RenderGraph rg, TextureHandle shadowMap, TextureHandle minMax,
			int block, int shadowRes, int coarseRes)
		{
			using var builder = rg.AddComputePass<PassData>("Shadow MinMax Pyramid", out var passData);

			passData.Cs = _cs;
			passData.Kernel = _kernel;
			passData.ShadowMap = shadowMap;
			passData.MinMax = minMax;
			passData.Dims = new Vector4(block, shadowRes, coarseRes, 0f);
			passData.Groups = (coarseRes + 7) / 8;

			// Read the rendered shadow depth (orders us after the shadow pass), write the
			// coarse texture.
			builder.UseTexture(shadowMap, AccessFlags.Read);
			builder.UseTexture(minMax, AccessFlags.Write);
			// The lit shader reads the result via a global RG can't see; publish it after this
			// pass and keep the pass alive (its only consumer is invisible to the graph).
			builder.SetGlobalTextureAfterPass(minMax, _globalId);
			builder.AllowPassCulling(false);

			builder.SetRenderFunc(static (PassData data, ComputeGraphContext ctx) =>
			{
				var cmd = ctx.cmd;
				cmd.SetComputeTextureParam(data.Cs, data.Kernel, _shadowDepthId, data.ShadowMap);
				cmd.SetComputeTextureParam(data.Cs, data.Kernel, _resultId, data.MinMax);
				cmd.SetComputeVectorParam(data.Cs, _dimsId, data.Dims);
				cmd.DispatchCompute(data.Cs, data.Kernel, data.Groups, data.Groups, 1);
			});
		}
	}
}
