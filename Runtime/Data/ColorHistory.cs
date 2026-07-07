using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Persistent, double-buffered PREVIOUS-FRAME color buffer for screen-space Radiance Cascades. The
	// RC gather marches this frame's depth but samples RADIANCE from last frame's finished HDR color
	// (that's what solves the forward-pipeline chicken-and-egg: there is no lit color mid-frame). So we
	// need one frame of color latency, which means a persistent resource — and RenderGraph textures are
	// transient (recycled every frame). Same shape as TemporalHistory: allocate RTHandles OUTSIDE the
	// graph, ping-pong two of them (read last frame's, write this frame's), and let this frame's write
	// become next frame's read.
	//
	// Simpler than TemporalHistory in two ways: (1) it stores RADIANCE (RGBA16F HDR), not a value+depth
	// pair, and (2) M1 does NOT reproject the history (the near-static top-down camera makes a direct
	// same-uv sample good enough), so we keep no prev view/view-proj matrices here.
	public class ColorHistory
	{
		private readonly RTHandle[] _buffers = new RTHandle[2];
		private readonly string _name;
		private int _writeIndex;
		private int _width, _height;
		private GraphicsFormat _format;

		// False until at least one frame has been captured, so the pipeline can leave the RC keyword
		// off on the very first frame (when the read buffer is still empty/garbage) and fall back to
		// the ambient/SH diffuse — no dark flash while history warms up.
		public bool HasHistory { get; private set; }

		public ColorHistory(string name)
		{
			_name = name;
		}

		// Ensures correctly-sized buffers exist and returns the raw read-side RTHandle (last frame's
		// color — bound directly on the gather material, since a persistent RT exists at record time)
		// plus the imported write handle (this frame's capture target). A size change or first use
		// drops history so we never sample a stale/wrong-res buffer.
		// format must match the buffer being captured into it (CameraColor), because the capture is a
		// raw CommandBuffer.CopyTexture — no shader, no conversion, no chance of a Y-flip — which
		// requires identical dimensions AND format. A format change (e.g. toggling HDR) reallocs.
		public void GetFrameTextures(RenderGraph rg, int width, int height, GraphicsFormat format,
			out RTHandle readRt, out TextureHandle write)
		{
			if (_buffers[0] == null || _width != width || _height != height || _format != format)
			{
				Realloc(width, height, format);
				HasHistory = false;
			}

			int readIndex = 1 - _writeIndex;
			readRt = _buffers[readIndex];
			write = rg.ImportTexture(_buffers[_writeIndex]);
		}

		// Call after recording the capture pass: what we just wrote becomes next frame's history.
		public void Advance()
		{
			_writeIndex = 1 - _writeIndex;
			HasHistory = true;
		}

		private void Realloc(int width, int height, GraphicsFormat format)
		{
			Release();
			_width = width;
			_height = height;
			_format = format;
			for (int i = 0; i < 2; i++)
			{
				_buffers[i] = RTHandles.Alloc(width, height,
					colorFormat: format, // matches CameraColor (HDR B10G11R11, or R8G8B8A8_SRGB)
					filterMode: FilterMode.Bilinear, // gathered at projected (sub-texel) hit UVs
					wrapMode: TextureWrapMode.Clamp,
					name: $"{_name}{i}");
			}
			_writeIndex = 0;
		}

		public void Release()
		{
			for (int i = 0; i < 2; i++)
			{
				_buffers[i]?.Release();
				_buffers[i] = null;
			}
			HasHistory = false;
		}
	}
}
