using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Drives the precompute-once IBL bakes. These resources (the env-BRDF LUT + the GGX-prefiltered
	// skybox cube) are produced once and consumed for many frames, so the bakes run as IMMEDIATE
	// compute dispatches OUTSIDE RenderGraph — a plain CommandBuffer on the context. That avoids the
	// RG-ordering trap where a graph pass writes a texture the lit shader later samples via an
	// invisible global (RG wouldn't know to schedule the bake first). Per-frame global binding of the
	// results happens separately.
	public class EnvironmentIBLPass
	{
		// The env-BRDF integral is smooth, so a small LUT is plenty. The prefiltered cube is low
		// frequency too (it's blurred), so 128 is ample for a top-down camera.
		private const int BrdfLutResolution = 128;
		private const int CubeResolution = 128;
		// Sharp source cube the skybox is captured into (prefilter input). Only needs enough resolution
		// to not alias the sun before the GGX blur; sampled at LOD 0 only, so no mip chain.
		private const int SourceCubeResolution = 256;
		// Importance samples per prefiltered texel (mip 0 is a straight copy, so this is for the
		// rougher mips where the GGX lobe is wide).
		private const int PrefilterSampleCount = 128;

		// Per-face WORLD->VIEW matrices for cube capture, indexed by (CubemapFace)i. These are Unity's own
		// authoritative bases (copied verbatim from core CubemapRender.CubemapFaceBases, which mirror the
		// D3D cubemap look/up convention). Hand-deriving them via LookAt + up-vectors is error-prone — the
		// up-vector signs and the render-vs-sample handedness must line up exactly, or individual faces come
		// out rotated/mirrored (the ±X faces did). Rotation-only: the skybox is at infinity, so no
		// translation. Matrix4x4(col0, col1, col2, col3) — arguments are COLUMNS.
		private static readonly Matrix4x4[] s_faceViews =
		{
			new(new(0f, 0f, -1f, 0f), new(0f, 1f, 0f, 0f),  new(-1f, 0f, 0f, 0f), new(0f, 0f, 0f, 1f)), // +X
			new(new(0f, 0f, 1f, 0f),  new(0f, 1f, 0f, 0f),  new(1f, 0f, 0f, 0f),  new(0f, 0f, 0f, 1f)), // -X
			new(new(1f, 0f, 0f, 0f),  new(0f, 0f, -1f, 0f), new(0f, -1f, 0f, 0f), new(0f, 0f, 0f, 1f)), // +Y
			new(new(1f, 0f, 0f, 0f),  new(0f, 0f, 1f, 0f),  new(0f, 1f, 0f, 0f),  new(0f, 0f, 0f, 1f)), // -Y
			new(new(1f, 0f, 0f, 0f),  new(0f, 1f, 0f, 0f),  new(0f, 0f, -1f, 0f), new(0f, 0f, 0f, 1f)), // +Z
			new(new(-1f, 0f, 0f, 0f), new(0f, 1f, 0f, 0f),  new(0f, 0f, 1f, 0f),  new(0f, 0f, 0f, 1f)), // -Z
		};

		// One-shot rebake request (concern: explicit control). Set by game code on level load / skybox
		// change; consumed on the next bake. Static so callers reach it without a pipeline-instance
		// handle. Distinct from ForceIblRebake (which re-bakes EVERY frame while held, for shader iteration).
		private static bool s_rebakeRequested;
		public static void RequestRebake() => s_rebakeRequested = true;

		private static readonly int _brdfLutResultId = Shader.PropertyToID("_BrdfLutResult");
		private static readonly int _brdfLutParamsId = Shader.PropertyToID("_BrdfLutParams");
		private static readonly int _sourceCubeId = Shader.PropertyToID("_SourceCube");
		private static readonly int _sourceHdrDecodeId = Shader.PropertyToID("_SourceHDRDecode");
		private static readonly int _prefilterParamsId = Shader.PropertyToID("_PrefilterParams");
		private static readonly int _prefilteredEnvId = Shader.PropertyToID("_PrefilteredEnv");
		private static readonly int _envBrdfLutId = Shader.PropertyToID("_EnvBrdfLut");
		private static readonly int _envMaxMipId = Shader.PropertyToID("_EnvMaxMip");
		private static readonly int _envIntensityId = Shader.PropertyToID("_EnvIntensity");
		private static readonly int _shCoefficientsId = Shader.PropertyToID("_TDRPSHCoefficients");
		private const string IBL_KEYWORD = "_ENV_IBL";

		// Scratch for the 7 packed SH-L2 coefficients (Unity shAr..shC layout). Reused each frame.
		private readonly Vector4[] _shCoefficients = new Vector4[7];

		private readonly ComputeShader _brdfLutCs;
		private readonly int _brdfLutKernel;
		// Prefilter is a raster material (rendered per cube face/mip), not a compute: writing cube-face
		// UAVs isn't supported (no RWTextureCube), but cube-face RENDER TARGETS are.
		private readonly Material _prefilterMaterial;

		// The pass is useful as long as the LUT can be baked (that alone drives the LUT debug view).
		public bool IsValid => _brdfLutCs != null && _brdfLutKernel >= 0;
		// True when the prefiltered specular cube is baked from a valid environment this frame — the
		// pipeline uses this to enable the specular IBL keyword / the prefiltered-env debug view.
		public bool EnvReady { get; private set; }

		public EnvironmentIBLPass(ComputeShader brdfLutCs, Material prefilterMaterial)
		{
			_brdfLutCs = brdfLutCs;
			_brdfLutKernel = brdfLutCs != null ? brdfLutCs.FindKernel("BrdfLutBake") : -1;
			_prefilterMaterial = prefilterMaterial;
		}

		// Ensures the persistent resources exist and bakes anything dirty. Called once per camera
		// before RenderGraph recording (it needs the ScriptableRenderContext for the immediate
		// dispatch + skybox capture). The LUT bakes once (environment-independent); the prefilter
		// re-runs only when the environment identity changes (skybox swap / asset change) or on request.
		public void BakeIfNeeded(ScriptableRenderContext context, EnvironmentIBL env, Camera camera,
			bool forceRebake = false)
		{
			EnvReady = false;

			if (!IsValid)
				return;

			env.EnsureResources(BrdfLutResolution, CubeResolution, SourceCubeResolution);

			// Drop the baked-state gates so the prefilter re-runs this frame. Two triggers: ForceIblRebake
			// (held on, re-bakes every frame — shader iteration) and a one-shot RequestRebake() (level
			// load / runtime skybox tweak where the Material reference didn't change). A plain skybox swap
			// changes the signature below and re-bakes on its own without either.
			if (forceRebake || ConsumeRebakeRequest())
				env.InvalidateBakes();

			var cmd = CommandBufferPool.Get("IBL Bake");

			if (!env.LutBaked)
			{
				cmd.SetComputeVectorParam(_brdfLutCs, _brdfLutParamsId,
					new Vector4(BrdfLutResolution, 0f, 0f, 0f));
				cmd.SetComputeTextureParam(_brdfLutCs, _brdfLutKernel, _brdfLutResultId, env.BrdfLut);
				int groups = (BrdfLutResolution + 7) / 8;
				cmd.DispatchCompute(_brdfLutCs, _brdfLutKernel, groups, groups, 1);
				env.MarkLutBaked();
			}

			// Choose the prefilter source and an identity for change-tracking:
			//  1. Explicit asset cube  -> use it directly (authored HDRI); decode via the engine values.
			//  2. Scene skybox         -> capture it ourselves into a linear-HDR cube (identity decode).
			//  3. Engine default cube  -> fallback, but only when it's a real texture (1x1 placeholder in
			//     some editor contexts). None of these -> specular stays off, shading uses hemisphere ambient.
			Texture source = null;
			Object signature = null;
			Vector4 decode = ReflectionProbe.defaultTextureHDRDecodeValues;
			bool captureSkybox = false;

			if (RenderSettings.skybox != null)
			{
				source = env.SourceCube;
				signature = RenderSettings.skybox; // Material ref: changes on skybox swap / level load
				decode = new Vector4(1f, 1f, 0f, 0f); // our capture is already linear radiance
				captureSkybox = true;
			}
			else
			{
				var def = ReflectionProbe.defaultTexture;
				if (def != null && def.width > 1)
				{
					source = def;
					signature = def;
				}
			}

			if (_prefilterMaterial != null && source != null)
			{
				if (!env.EnvBakedFrom(signature))
				{
					// Render the current skybox into our source cube first (both recorded on `cmd`, so the
					// GPU runs capture -> prefilter in order — the prefilter's read sees the fresh faces).
					// Cold-start guard: on a fresh editor open the first camera through the pipeline can be a
					// preview/thumbnail cam that can't draw the skybox, or the (procedural) skybox shader may
					// still be async-compiling from a cold cache. Capturing then would bake an EMPTY cube and,
					// because we'd mark it baked, never retry — reflections stay absent until a manual rebake.
					// So only mark baked once the capture actually succeeded and no shaders are still compiling;
					// otherwise leave it unbaked and try again next frame (it self-heals within a frame or two).
					bool ready = !captureSkybox || CaptureSkybox(context, cmd, env, camera);
#if UNITY_EDITOR
					ready &= !UnityEditor.ShaderUtil.anythingCompiling;
#endif
					if (ready)
					{
						PrefilterEnvironment(cmd, env, source, decode);
						// Refresh the SH diffuse source alongside the specular prefilter. RenderSettings.ambientProbe
						// is Unity ENVIRONMENT state that a custom SRP does NOT maintain — with no baked/auto lighting
						// it sits at zero, making the IBL diffuse (SampleSHIrradiance) black and crushing shadows.
						// Regenerated from RenderSettings.skybox. Tied to the (infrequent) env re-bake, so it also
						// re-runs on domain reload (a fresh EnvironmentIBL has no baked source yet). Immediate call,
						// outside RenderGraph — never from inside a pass.
						DynamicGI.UpdateEnvironment();
						env.MarkEnvBaked(signature);
					}
				}
				// Only expose the specular cube once it's actually baked from this source — a half-captured
				// or still-pending cube must fall back to the hemisphere ambient, not show black reflections.
				EnvReady = env.EnvBakedFrom(signature);
			}

			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
		}

		private static bool ConsumeRebakeRequest()
		{
			bool requested = s_rebakeRequested;
			s_rebakeRequested = false;
			return requested;
		}

		// Renders the active skybox into the persistent source cube, one face at a time. Uses the engine's
		// own skybox renderer list (via a per-face rotation-only view + 90° projection) so it handles any
		// skybox type — procedural, cubemap, 6-sided — without us reimplementing sky math. This replaces
		// the old frozen offline capture; because it re-runs on the env re-bake it always tracks the
		// current sky, and it stays on `cmd` so the prefilter that follows reads the fresh faces.
		// Returns true only if every face drew a valid skybox list — the caller uses that to avoid locking
		// in an empty cube when the environment isn't ready yet (see the cold-start note in BakeIfNeeded).
		private bool CaptureSkybox(ScriptableRenderContext context, CommandBuffer cmd, EnvironmentIBL env,
			Camera camera)
		{
			if (camera == null)
				return false;

			var cube = env.SourceCube;
			// 90° FOV square frustum covers exactly one cube face. GL.GetGPUProjectionMatrix(_, true)
			// bakes in the platform's render-INTO-texture clip convention (notably the Y flip) — the
			// DrawRendererList path writes Y-flipped without it (faces come out upside down).
			var proj = Matrix4x4.Perspective(90f, 1f, 0.1f, 10f);
			var gpuProj = GL.GetGPUProjectionMatrix(proj, true);

			for (int face = 0; face < 6; face++)
			{
				var view = s_faceViews[face];

				var skyList = context.CreateSkyboxRendererList(camera, gpuProj, view);
				if (!skyList.isValid)
					return false; // camera/skybox not ready to draw — bail so we retry a later frame
				cmd.SetRenderTarget(cube.rt, 0, (CubemapFace)face);
				// DrawRendererList uses the command buffer's CURRENTLY-BOUND view/projection — the matrices
				// baked into the renderer list drive setup/culling, not the GPU transform. Without this the
				// skybox draws with a stale VP (constant direction -> a uniform cube).
				cmd.SetViewProjectionMatrices(view, gpuProj);
				cmd.DrawRendererList(skyList);
			}
			return true;
		}

		private void PrefilterEnvironment(CommandBuffer cmd, EnvironmentIBL env, Texture source, Vector4 decode)
		{
			// Source cube + HDR decode as globals (the material reads them); the command buffer records
			// SetGlobal + Draw in order, so per-face/mip params can't race (unlike material.SetX).
			cmd.SetGlobalTexture(_sourceCubeId, source);
			cmd.SetGlobalVector(_sourceHdrDecodeId, decode);

			var cube = env.PrefilteredEnv;
			int mipCount = env.CubeMipCount;
			int maxMip = env.CubeMaxMip;
			for (int mip = 0; mip < mipCount; mip++)
			{
				// Perceptual roughness spread linearly over the mip chain (mip 0 = mirror).
				float roughness = maxMip > 0 ? (float)mip / maxMip : 0f;
				for (int face = 0; face < 6; face++)
				{
					cmd.SetGlobalVector(_prefilterParamsId,
						new Vector4(face, roughness, PrefilterSampleCount, 0f));
					// Target this cube face at this mip; the fullscreen triangle covers the face.
					cmd.SetRenderTarget(cube.rt, mip, (CubemapFace)face);
					cmd.DrawProcedural(Matrix4x4.identity, _prefilterMaterial, 0,
						MeshTopology.Triangles, 3);
				}
			}
		}

		private class BindPassData
		{
			public bool Active;
			public RTHandle PrefilteredEnv;
			public RTHandle BrdfLut;
			public float MaxMip;
			public float Intensity;
			public Vector4[] Sh;
		}

		// Packs RenderSettings.ambientProbe (SH-L2 of the environment) into the 7-vector shAr..shC
		// layout that core's SampleSH9 / SHEvalLinearL0L1+L2 expect. This is Unity's standard
		// GetShaderConstantsFromNormalizedSH — the same packing URP uses for its ambient SH. The probe
		// already folds in the scene's ambient intensity and (in Skybox ambient mode) the sky's colour.
		private void PackAmbientSH()
		{
			var probe = RenderSettings.ambientProbe;
			for (int c = 0; c < 3; c++)
			{
				// Constant + linear (L0, L1): (y1, y-1, y0-ish reorder) with the L0 term adjusted by L2.
				_shCoefficients[c] = new Vector4(probe[c, 3], probe[c, 1], probe[c, 2],
					probe[c, 0] - probe[c, 6]);
				// Quadratic (L2), 4 of the 5 terms; the 5th goes in shC below.
				_shCoefficients[c + 3] = new Vector4(probe[c, 4], probe[c, 5], probe[c, 6] * 3f,
					probe[c, 7]);
			}
			_shCoefficients[6] = new Vector4(probe[0, 8], probe[1, 8], probe[2, 8], 1f);
		}

		// Per-frame global bind of the (persistent) IBL resources + the _ENV_IBL keyword, so the lit
		// shaders sample the prefiltered cube / BRDF LUT. Mirrors SetupLightingPass: an unsafe pass with
		// global-state modification, not culled. When inactive it just clears the keyword (shading falls
		// back to the hemisphere ambient), so it's always recorded to keep the keyword correct.
		public void Record(RenderGraph rg, EnvironmentIBL env, in FrameContext frame, bool iblActive)
		{
			using var builder = rg.AddUnsafePass<BindPassData>("IBL Bind", out var data);

			data.Active = iblActive;
			data.PrefilteredEnv = env.PrefilteredEnv;
			data.BrdfLut = env.BrdfLut;
			data.MaxMip = env.CubeMaxMip;
			data.Intensity = frame.Settings.EnvIntensity;
			if (iblActive)
				PackAmbientSH();
			data.Sh = _shCoefficients;

			builder.AllowPassCulling(false);
			builder.AllowGlobalStateModification(true);

			builder.SetRenderFunc(static (BindPassData d, UnsafeGraphContext ctx) =>
			{
				var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
				if (d.Active)
				{
					// RTHandle -> RenderTargetIdentifier (these are persistent, non-RG textures).
					cmd.SetGlobalTexture(_prefilteredEnvId, d.PrefilteredEnv);
					cmd.SetGlobalTexture(_envBrdfLutId, d.BrdfLut);
					cmd.SetGlobalFloat(_envMaxMipId, d.MaxMip);
					cmd.SetGlobalFloat(_envIntensityId, d.Intensity);
					cmd.SetGlobalVectorArray(_shCoefficientsId, d.Sh);
				}
				CoreUtils.SetKeyword(cmd, IBL_KEYWORD, d.Active);
			});
		}
	}
}
