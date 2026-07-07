using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Compute pass: assigns each visible punctual light to the clusters it can reach,
	// writing a per-cluster count (light grid) + index list. This is the heart of
	// forward+; the fragment shader later reads these lists instead of looping all lights.
	public class ClusterLightCullPass
	{
		private static readonly int _punctualLightsId = Shader.PropertyToID("_PunctualLights");
		private static readonly int _punctualLightCountId = Shader.PropertyToID("_PunctualLightCount");
		private static readonly int _invProjectionId = Shader.PropertyToID("_ClusterInvProjection");
		private static readonly int _worldToViewId = Shader.PropertyToID("_ClusterWorldToView");
		private static readonly int _nearFarId = Shader.PropertyToID("_ClusterNearFar");
		private static readonly int _lightGridId = Shader.PropertyToID("_ClusterLightGrid");
		private static readonly int _lightIndicesId = Shader.PropertyToID("_ClusterLightIndices");
		private static readonly int _screenDimensionsId = Shader.PropertyToID("_ClusterScreenDimensions");

		private readonly ComputeShader _cs;
		private readonly int _kernel;

		public bool IsValid => _cs != null && _kernel >= 0;

		public ClusterLightCullPass(ComputeShader cs)
		{
			_cs = cs;
			_kernel = cs != null ? cs.FindKernel("ClusterCull") : -1;
		}

		private class PassData
		{
			public ComputeShader Cs;
			public int Kernel;
			public GraphicsBuffer Lights;
			public int LightCount;
			public Matrix4x4 InvProjection;
			public Matrix4x4 WorldToView;
			public Vector4 NearFar;
			public Vector4 ScreenDimensions;
			public BufferHandle LightGrid;
			public BufferHandle LightIndices;
			public int GroupCount;
		}

		public void Record(RenderGraph rg, in FrameContext frame, GraphicsBuffer lights, int lightCount,
			BufferHandle lightGrid, BufferHandle lightIndices)
		{
			if (!IsValid || lights == null)
				return;

			using var builder = rg.AddComputePass<PassData>("Cluster Light Cull", out var passData);

			passData.Cs = _cs;
			passData.Kernel = _kernel;
			passData.Lights = lights;
			passData.LightCount = lightCount;
			// CPU-side (OpenGL-convention) projection inverse: platform independent, and
			// we only use it for the XY ray direction so reverse-Z does not matter here.
			passData.InvProjection = frame.Camera.projectionMatrix.inverse;
			passData.WorldToView = frame.Camera.worldToCameraMatrix;
			passData.NearFar = new Vector4(frame.Camera.nearClipPlane, frame.Camera.farClipPlane, 0f, 0f);
			// Render resolution (== display at scale 1). The lit shader maps a pixel to a cluster tile via
			// svPosition / _ClusterScreenDimensions, and svPosition is in the render-res grid — so this
			// MUST be render res, or the tile lookup and this cull's tile assignment would disagree.
			passData.ScreenDimensions = new Vector4(frame.RenderWidth, frame.RenderHeight, 0f, 0f);
			passData.LightGrid = lightGrid;
			passData.LightIndices = lightIndices;
			passData.GroupCount = (ClusterConfig.ClusterCount + 63) / 64;

			// Declare the RG buffer writes so RenderGraph orders this before any reader
			// (the debug/lit pass) and does not cull the dispatch.
			builder.UseBuffer(lightGrid, AccessFlags.Write);
			builder.UseBuffer(lightIndices, AccessFlags.Write);

			// We also bind the cluster buffers + lookup params as GLOBALS so the lit shader
			// (run by the Opaque raster pass, which can't set globals itself) can read them.
			// Those reads are invisible to RG, so keep the pass alive explicitly; the Opaque
			// pass declares matching UseBuffer(Read) calls to extend the buffers' lifetime
			// and order this dispatch before shading.
			builder.AllowPassCulling(false);
			builder.AllowGlobalStateModification(true);

			builder.SetRenderFunc(static (PassData data, ComputeGraphContext ctx) =>
			{
				var cmd = ctx.cmd;
				cmd.SetComputeBufferParam(data.Cs, data.Kernel, _punctualLightsId, data.Lights);
				cmd.SetComputeIntParam(data.Cs, _punctualLightCountId, data.LightCount);
				cmd.SetComputeMatrixParam(data.Cs, _invProjectionId, data.InvProjection);
				cmd.SetComputeMatrixParam(data.Cs, _worldToViewId, data.WorldToView);
				cmd.SetComputeVectorParam(data.Cs, _nearFarId, data.NearFar);
				cmd.SetComputeBufferParam(data.Cs, data.Kernel, _lightGridId, data.LightGrid);
				cmd.SetComputeBufferParam(data.Cs, data.Kernel, _lightIndicesId, data.LightIndices);
				cmd.DispatchCompute(data.Cs, data.Kernel, data.GroupCount, 1, 1);

				// Bind for the fragment-side consumer. BufferHandle implicitly resolves to
				// the realized GraphicsBuffer here.
				cmd.SetGlobalBuffer(_lightGridId, data.LightGrid);
				cmd.SetGlobalBuffer(_lightIndicesId, data.LightIndices);
				cmd.SetGlobalVector(_screenDimensionsId, data.ScreenDimensions);
				cmd.SetGlobalVector(_nearFarId, data.NearFar);
			});
		}
	}
}
