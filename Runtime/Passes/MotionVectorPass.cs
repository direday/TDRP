using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Builds the per-pixel screen-space velocity buffer (_CameraMotionVectorsTexture): how far each
	// surface point moved on screen since the previous frame, stored in UV space so that
	//     prevUV = currentUV - motion
	// (Unity's convention — the same delta a later temporal-reprojection step will consume).
	//
	// Two contributions, matching how URP/HDRP build this:
	//   1. CAMERA motion (fullscreen): even a fully static scene moves on screen when the camera
	//      moves. Recoverable from depth alone — reconstruct world pos from CameraDepth, project it
	//      through LAST frame's view-proj, and the delta from the current UV is the velocity. One
	//      fullscreen draw, fills every pixel (including sky, which correctly slides under rotation).
	//   2. OBJECT motion (per-renderer): a moving/rotating object moves independently of the camera;
	//      depth reprojection can't see that (it assumes a static world). Each renderer's MotionVectors
	//      shader pass recomputes its clip position with the PREVIOUS-frame model matrix. Unity's
	//      engine maintains that matrix for us and binds it as unity_MatrixPreviousM when the draw is
	//      configured with PerObjectData.MotionVectors. These draws overwrite the camera-motion fill
	//      on the moving objects' pixels (ZTest Equal against the depth prepass, so only visible
	//      fragments write).
	//
	// For a STATIC object the object pass reproduces the camera-motion fill exactly (prevM == currM),
	// so overwriting it is harmless — we don't bother filtering static renderers out.
	public class MotionVectorPass
	{
		private static readonly int _prevViewProjId = Shader.PropertyToID("_PrevViewProjMatrix");
		private static readonly int _currInvViewProjId = Shader.PropertyToID("_CurrInvViewProjMatrix");
		private static readonly int _paramsId = Shader.PropertyToID("_MotionVectorParams");
		private static readonly int _motionVectorsTextureId = Shader.PropertyToID("_CameraMotionVectorsTexture");

		private static readonly ShaderTagId[] _objectTags =
		{
			new("MotionVectors"),
		};

		private readonly Material _cameraMotionMaterial;
		// Per-camera previous view-projection. Like the temporal histories, this MUST be per-camera:
		// the Scene-view and Game-view cameras render every editor repaint with different matrices, and
		// a shared "prev VP" would make each reproject against the other camera's frame. Keyed by
		// camera id, evicted when a camera goes quiet (transient preview cameras).
		private readonly Dictionary<int, CameraPrev> _prevByCamera = new();
		private readonly List<int> _stale = new();

		private const int EvictAfterFrames = 60;

		private class CameraPrev
		{
			public Matrix4x4 ViewProj;
			public bool Valid;
			public int LastUsedFrame;
		}

		public bool IsValid => _cameraMotionMaterial != null;

		public MotionVectorPass(Material cameraMotionMaterial)
		{
			_cameraMotionMaterial = cameraMotionMaterial;
		}

		private class CameraPassData
		{
			public Material Material;
		}

		private class ObjectPassData
		{
			public RendererListHandle RendererList;
			public Matrix4x4 PrevViewProj;
		}

		public void Record(RenderGraph rg, in FrameContext frame, TextureHandle motionVectors, TextureHandle depth)
		{
			var prev = GetCameraPrev(frame.Camera.GetEntityId(), frame.FrameIndex);

			// GPU-corrected matrices (clip-space handedness / reversed-Z as the shader sees them),
			// matching what SetupCameraProperties baked into the depth we sample.
			var gpuProj = GL.GetGPUProjectionMatrix(frame.Camera.projectionMatrix, true);
			var view = frame.Camera.worldToCameraMatrix;
			var viewProj = gpuProj * view;

			// First frame for this camera (or after eviction): no valid history, so treat "prev" as
			// "curr" — every motion is then 0, which is the correct, artifact-free default.
			var prevViewProj = prev.Valid ? prev.ViewProj : viewProj;

			// --- Pass 1: camera motion, fullscreen from depth. Writes every pixel (ZTest Always),
			// so the buffer needs no clear. ---
			_cameraMotionMaterial.SetMatrix(_prevViewProjId, prevViewProj);
			_cameraMotionMaterial.SetMatrix(_currInvViewProjId, viewProj.inverse);
			_cameraMotionMaterial.SetVector(_paramsId, new Vector4(prev.Valid ? 1f : 0f, 0f, 0f, 0f));

			using (var builder = rg.AddRasterRenderPass<CameraPassData>("Motion Vectors (Camera)", out var passData))
			{
				passData.Material = _cameraMotionMaterial;

				builder.SetRenderAttachment(motionVectors, 0);
				// Depth is sampled as the global _CameraDepthTexture; declare the read so RG keeps it
				// alive and orders the prepass before us.
				builder.UseTexture(depth, AccessFlags.Read);

				builder.SetRenderFunc(static (CameraPassData data, RasterGraphContext ctx) =>
				{
					ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Material, 0, MeshTopology.Triangles, 3);
				});
			}

			// --- Pass 2: object motion, per-renderer. Overwrites the moving objects' pixels with
			// their true per-object velocity. ZTest Equal (via the shader) leans on the depth prepass,
			// so only the visible fragment of each object writes. ---
			using (var builder = rg.AddRasterRenderPass<ObjectPassData>("Motion Vectors (Objects)", out var passData))
			{
				var desc = new RendererListDesc(_objectTags, frame.CullingResults, frame.Camera)
				{
					renderQueueRange = RenderQueueRange.opaque,
					sortingCriteria = SortingCriteria.CommonOpaque,
					// The reason this whole pass works: asks the engine to bind each renderer's
					// previous-frame model matrix (unity_MatrixPreviousM) for the draw.
					rendererConfiguration = PerObjectData.MotionVectors,
				};

				passData.RendererList = rg.CreateRendererList(desc);
				passData.PrevViewProj = prevViewProj;

				builder.UseRendererList(passData.RendererList);
				builder.SetRenderAttachment(motionVectors, 0);
				// Read-only depth: the shader's ZTest Equal needs it bound, but we don't write it.
				builder.SetRenderAttachmentDepth(depth, AccessFlags.Read);
				// The object shader reads the previous camera view-proj as a global; setting it here
				// (with global-state modification allowed) keeps it beside the draw that consumes it.
				builder.AllowGlobalStateModification(true);
				// Publish the finished buffer as a global for the debug view (and, later, the temporal
				// reprojection passes). Set after this pass — the last one to write the attachment.
				builder.SetGlobalTextureAfterPass(motionVectors, _motionVectorsTextureId);

				builder.SetRenderFunc(static (ObjectPassData data, RasterGraphContext ctx) =>
				{
					ctx.cmd.SetGlobalMatrix(_prevViewProjId, data.PrevViewProj);
					ctx.cmd.DrawRendererList(data.RendererList);
				});
			}

			// This frame's VP becomes next frame's "prev".
			prev.ViewProj = viewProj;
			prev.Valid = true;
		}

		private CameraPrev GetCameraPrev(int cameraId, int frame)
		{
			Evict(frame);

			if (!_prevByCamera.TryGetValue(cameraId, out var entry))
			{
				entry = new CameraPrev();
				_prevByCamera[cameraId] = entry;
			}
			entry.LastUsedFrame = frame;
			return entry;
		}

		private void Evict(int frame)
		{
			_stale.Clear();
			foreach (var kvp in _prevByCamera)
			{
				if (frame - kvp.Value.LastUsedFrame > EvictAfterFrames)
					_stale.Add(kvp.Key);
			}
			foreach (int key in _stale)
				_prevByCamera.Remove(key);
		}
	}
}
