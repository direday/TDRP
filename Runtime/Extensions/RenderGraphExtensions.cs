using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	public static class RenderGraphExtensions
	{
		public static RendererListHandle CreateRendererList(this RenderGraph rg, 
			ShaderTagId[] tags, CullingResults cullingResults, Camera camera, RenderQueueRange range, SortingCriteria sorting)
		{
			var desc = new RendererListDesc(tags, cullingResults, camera)
			{
				renderQueueRange = range,
				sortingCriteria = sorting,
			};

			return rg.CreateRendererList(desc);
		}
	}
}