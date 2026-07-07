using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Builds one cascade of the Radiance Cascades hierarchy (and merges the cascade above into it).
	// Recorded once per cascade, TOP-DOWN (highest index first). Each build publishes its texture as the
	// global _RcUpperCascade so the next (lower) build can continue rays into it; cascade 0 additionally
	// publishes _RcCascade0 for the resolve.
	//
	// The per-cascade scalars (block size / spacing / interval / upper-grid dims) differ per Record but
	// all passes share one material, so those live on a per-cascade MaterialPropertyBlock (the same
	// shared-material race the separable blurs hit). The matrices / screen size / bias are identical
	// every cascade -> material. The upper cascade is read as the _RcUpperCascade GLOBAL (a transient RG
	// texture can't be bound on a material), chained via SetGlobalTextureAfterPass. Ambient colours come
	// from the globals SetupLightingPass already binds.
	public class RcCascadeBuildPass
	{
		private static readonly int _screenToWorldId = Shader.PropertyToID("_ScreenToWorld");
		private static readonly int _worldToScreenId = Shader.PropertyToID("_WorldToScreen");
		private static readonly int _rcPrevWorldToScreenId = Shader.PropertyToID("_RcPrevWorldToScreen");
		private static readonly int _rcViewId = Shader.PropertyToID("_RcView");
		private static readonly int _rcPrevColorId = Shader.PropertyToID("_RcPrevColor");
		private static readonly int _rcScreenSizeId = Shader.PropertyToID("_RcScreenSize");
		private static readonly int _rcMiscId = Shader.PropertyToID("_RcMisc");
		private static readonly int _rcJitterId = Shader.PropertyToID("_RcJitter");
		private static readonly int _rcCascadeId = Shader.PropertyToID("_RcCascade");
		private static readonly int _rcIntervalId = Shader.PropertyToID("_RcInterval");
		private static readonly int _rcUpperId = Shader.PropertyToID("_RcUpper");
		private static readonly int _rcUpperCascadeId = Shader.PropertyToID("_RcUpperCascade");
		private static readonly int _rcCascade0Id = Shader.PropertyToID("_RcCascade0");

		private const int MaxCascades = 6;

		private readonly Material _material;
		private readonly MaterialPropertyBlock[] _props = new MaterialPropertyBlock[MaxCascades];

		// Last frame's view-projection per camera (keyed by entity id, like the temporal histories).
		// Per-camera because Scene and Game views must not share it — they'd stomp each other's motion
		// and reproject with the wrong matrix (same rule as TemporalHistoryCache).
		private readonly Dictionary<int, Matrix4x4> _prevViewProj = new();

		public bool IsValid => _material != null;

		public RcCascadeBuildPass(Material material)
		{
			_material = material;
			for (int i = 0; i < MaxCascades; i++)
				_props[i] = new MaterialPropertyBlock();
		}

		// Call ONCE per camera per frame, before recording that camera's cascades. Returns last frame's
		// VP (or the current one on the first frame / after a camera cut = no reprojection) and stores
		// the current one for next frame. The stored VP stays in lockstep with the colour history
		// (both advance once per RC record), so they always describe the same previous frame.
		public Matrix4x4 UpdateAndGetPrevViewProj(int cameraId, Matrix4x4 currentViewProj)
		{
			if (!_prevViewProj.TryGetValue(cameraId, out var prev))
				prev = currentViewProj;
			_prevViewProj[cameraId] = currentViewProj;
			return prev;
		}

		private class PassData
		{
			public Material Material;
			public MaterialPropertyBlock Props;
		}

		public struct CascadeDesc
		{
			public int Index;
			public int BlockSide;   // d = directions per axis
			public float Spacing;   // s = probe spacing in screen pixels
			public int Directions;  // D = d*d
			public float IntervalStart, IntervalEnd; // [t0, t1] in world units
			public int Steps;
			public bool Merge;
			// Upper (i+1) cascade layout for the merge (0 when there is no upper).
			public int UpperBlockSide;
			public float UpperSpacing;
			public int UpperTexW, UpperTexH;
		}

		public void Record(RenderGraph rg, in FrameContext frame, in CascadeDesc c, TextureHandle cascadeTex,
			TextureHandle upper, TextureHandle depth, RTHandle prevColor, Matrix4x4 prevViewProj, float bias,
			float skyFloor, float falloffDistance, float fireflyClamp, Vector2 jitter, int screenW, int screenH)
		{
			var proj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true);
			var view = frame.Camera.worldToCameraMatrix;
			var viewProj = proj * view;
			_material.SetMatrix(_screenToWorldId, viewProj.inverse);
			_material.SetMatrix(_worldToScreenId, viewProj);
			_material.SetMatrix(_rcPrevWorldToScreenId, prevViewProj);
			_material.SetMatrix(_rcViewId, view);
			_material.SetTexture(_rcPrevColorId, prevColor);
			_material.SetVector(_rcScreenSizeId, new Vector4(screenW, screenH, 1f / screenW, 1f / screenH));
			_material.SetVector(_rcMiscId, new Vector4(bias, skyFloor, falloffDistance, fireflyClamp));
			// Same jitter for every cascade this frame (the merge inverts it per level) — set on the shared
			// material once per Record; harmless to repeat since it's constant across the hierarchy.
			_material.SetVector(_rcJitterId, jitter);

			bool hasUpper = c.Merge && upper.IsValid();
			var props = _props[c.Index];
			props.SetVector(_rcCascadeId, new Vector4(c.BlockSide, c.Spacing, c.Directions, c.Index));
			props.SetVector(_rcIntervalId, new Vector4(c.IntervalStart, c.IntervalEnd, c.Steps, hasUpper ? 1f : 0f));
			props.SetVector(_rcUpperId, new Vector4(c.UpperBlockSide, c.UpperSpacing, c.UpperTexW, c.UpperTexH));

			using var builder = rg.AddRasterRenderPass<PassData>($"RC Cascade {c.Index}", out var passData);

			passData.Material = _material;
			passData.Props = props;

			builder.SetRenderAttachment(cascadeTex, 0);
			builder.UseTexture(depth, AccessFlags.Read);
			if (hasUpper)
				builder.UseTexture(upper, AccessFlags.Read); // read as the global _RcUpperCascade

			builder.SetGlobalTextureAfterPass(cascadeTex, _rcUpperCascadeId); // for the next lower cascade
			if (c.Index == 0)
				builder.SetGlobalTextureAfterPass(cascadeTex, _rcCascade0Id); // for the resolve

			builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
			{
				ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3, 1, data.Props);
			});
		}
	}
}
