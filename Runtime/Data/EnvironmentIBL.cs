using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace TDRP
{
	// Owns the persistent, precompute-once image-based-lighting resources: the environment-BRDF LUT
	// and the GGX-prefiltered environment cubemap. Like TemporalHistory these live OUTSIDE RenderGraph
	// (rg.CreateTexture memory is transient/recycled every frame) as RTHandles bound directly. Unlike
	// the temporal histories they don't ping-pong; the LUT is baked once (it's environment-independent)
	// and the prefiltered cube is rebuilt only when the source environment changes.
	public class EnvironmentIBL
	{
		private RTHandle _brdfLut;
		private int _lutRes;

		private RTHandle _prefilteredEnv;
		private int _cubeRes;
		private int _mipCount;
		// Identity of the environment the cube was last prefiltered from: the explicit cube Texture, or
		// the skybox Material when we captured it. When this no longer matches the active environment
		// (asset/skybox swap, first availability) we re-bake.
		private Object _bakedEnvSource;

		// Persistent linear-HDR cube the pipeline renders the current skybox into (per face), used as
		// the prefilter source when there's no explicit environment cubemap assigned. Replaces the old
		// frozen offline capture. Sampled only at LOD 0 by the prefilter, so it carries no mip chain.
		private RTHandle _sourceCube;
		private int _sourceRes;

		public bool LutBaked { get; private set; }

		public RTHandle BrdfLut => _brdfLut;
		public RTHandle PrefilteredEnv => _prefilteredEnv;
		public RTHandle SourceCube => _sourceCube;
		public int CubeMipCount => _mipCount;
		public int CubeMaxMip => Mathf.Max(0, _mipCount - 1);

		// Ensures the persistent resources exist at the requested sizes. A size change frees and
		// reallocates, invalidating the relevant baked state.
		public void EnsureResources(int lutRes, int cubeRes, int sourceRes)
		{
			if (_brdfLut == null || _lutRes != lutRes)
			{
				_brdfLut?.Release();
				_lutRes = lutRes;
				// RG16F holds the split-sum (scale, bias) in [0,1]; UAV so the bake compute can write it.
				_brdfLut = RTHandles.Alloc(lutRes, lutRes,
					colorFormat: GraphicsFormat.R16G16_SFloat,
					filterMode: FilterMode.Bilinear,
					wrapMode: TextureWrapMode.Clamp,
					enableRandomWrite: true,
					name: "EnvBrdfLut");
				LutBaked = false;
			}

			if (_prefilteredEnv == null || _cubeRes != cubeRes)
			{
				_prefilteredEnv?.Release();
				_cubeRes = cubeRes;
				// HDR cube with a full roughness mip chain. Trilinear so PerceptualRoughnessToMipmapLevel
				// blends between mips; autoGenerateMips off — WE write every mip (each is a different
				// roughness convolution, not a box downsample).
				_prefilteredEnv = RTHandles.Alloc(cubeRes, cubeRes, slices: 1,
					colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
					filterMode: FilterMode.Trilinear,
					wrapMode: TextureWrapMode.Clamp,
					dimension: TextureDimension.Cube,
					enableRandomWrite: true,
					useMipMap: true,
					autoGenerateMips: false,
					name: "PrefilteredEnv");
				_mipCount = 1 + Mathf.FloorToInt(Mathf.Log(cubeRes, 2f));
				_bakedEnvSource = null; // force a re-bake into the new allocation
			}

			if (_sourceCube == null || _sourceRes != sourceRes)
			{
				_sourceCube?.Release();
				_sourceRes = sourceRes;
				// Linear HDR, no mip chain: the prefilter samples this at LOD 0 only (it does its own
				// GGX integration). Not a UAV — we render into it per face via SetRenderTarget, not compute.
				_sourceCube = RTHandles.Alloc(sourceRes, sourceRes, slices: 1,
					colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
					filterMode: FilterMode.Bilinear,
					wrapMode: TextureWrapMode.Clamp,
					dimension: TextureDimension.Cube,
					useMipMap: false,
					name: "EnvSourceCube");
				_bakedEnvSource = null; // the source we prefilter from changed identity
			}
		}

		public void MarkLutBaked() => LutBaked = true;

		// Debug: drop both baked-state gates so the next BakeIfNeeded re-runs the LUT compute AND the
		// prefilter, without reallocating (the resources are unchanged). Lets you iterate on the bake
		// shaders / alloc flags and see the result live, where the normal gates would skip the rebake
		// (the LUT is once-ever; the prefilter only re-runs when the source texture changes).
		public void InvalidateBakes()
		{
			LutBaked = false;
			_bakedEnvSource = null;
		}

		// The prefiltered cube is valid iff it was baked from the currently-active environment source.
		// `source` is a stand-in identity for the environment: the explicit cube's Texture reference,
		// or (for a captured skybox) the skybox Material. A change in identity — asset swap, skybox
		// swap on level load — invalidates the bake. InvalidateBakes()/a one-shot request set it null.
		public bool EnvBakedFrom(Object source) => _bakedEnvSource != null && _bakedEnvSource == source;

		public void MarkEnvBaked(Object source) => _bakedEnvSource = source;

		public void Release()
		{
			_brdfLut?.Release();
			_brdfLut = null;
			LutBaked = false;

			_prefilteredEnv?.Release();
			_prefilteredEnv = null;
			_bakedEnvSource = null;

			_sourceCube?.Release();
			_sourceCube = null;
		}
	}
}
