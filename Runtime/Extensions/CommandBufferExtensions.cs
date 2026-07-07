using UnityEngine.Rendering;

namespace TDRP
{
	public static class CommandBufferExtensions
	{
		public static void ExecuteAndClear(this CommandBuffer buffer, ScriptableRenderContext context)
		{
			context.ExecuteCommandBuffer(buffer);
			buffer.Clear();
		}
	}
}
