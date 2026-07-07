using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	public class RenderGraphResources
	{
		public TextureHandle CameraColor;
		public TextureHandle CameraColorLdr;
		public TextureHandle SmaaEdges;
		public TextureHandle SmaaWeights;
		public TextureHandle CameraDepth;
		public TextureHandle CameraNormals;
		public TextureHandle CameraMotionVectors;
		public TextureHandle DirectionalShadowMap;
		public TextureHandle ShadowMinMax;
		public TextureHandle ScreenShadowMask;
		public TextureHandle AmbientOcclusion;
		public TextureHandle RcIrradiance;
		public TextureHandle OitAccum;
		public TextureHandle OitRevealage;
		public BufferHandle ClusterLightGrid;
		public BufferHandle ClusterLightIndices;

		public void Reset()
		{
			CameraColor = TextureHandle.nullHandle;
			CameraColorLdr = TextureHandle.nullHandle;
			SmaaEdges = TextureHandle.nullHandle;
			SmaaWeights = TextureHandle.nullHandle;
			CameraDepth = TextureHandle.nullHandle;
			CameraNormals = TextureHandle.nullHandle;
			CameraMotionVectors = TextureHandle.nullHandle;
			DirectionalShadowMap = TextureHandle.nullHandle;
			ShadowMinMax = TextureHandle.nullHandle;
			ScreenShadowMask = TextureHandle.nullHandle;
			AmbientOcclusion = TextureHandle.nullHandle;
			RcIrradiance = TextureHandle.nullHandle;
			OitAccum = TextureHandle.nullHandle;
			OitRevealage = TextureHandle.nullHandle;
			ClusterLightGrid = BufferHandle.nullHandle;
			ClusterLightIndices = BufferHandle.nullHandle;
		}
	}
}
