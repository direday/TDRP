using UnityEngine;
using UnityEngine.Rendering;

namespace TDRP
{
	public struct FrameContext
	{
		public Camera Camera;
		public CullingResults CullingResults;
		public PipelineSettings Settings;
		// Editor-safe render counter (advances every camera render, unlike Time.frameCount which
		// freezes on editor one-shot renders). Used to key per-frame jitter and per-camera history
		// eviction. Set by the pipeline each RenderCamera.
		public int FrameIndex;
		// Internal render resolution = Camera.pixelWidth/Height * Settings.RenderScale (clamped >=1).
		// EVERY offscreen buffer (CameraColor/Depth/Normals and everything derived) is sized to this,
		// NOT to the camera's pixel size — that's what makes render scale a single knob. Only the final
		// backbuffer stays at the camera's display size; the present pass upscales render->display.
		// Screen-space consumers all LOAD by SV_Position (which is in this render-res grid), so the
		// pixel indices line up automatically without any _ScreenParams fixups.
		public int RenderWidth;
		public int RenderHeight;
	}
}
