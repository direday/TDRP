using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace TDRP
{
	// Owns a persistent, double-buffered history texture for temporal accumulation. Shared by any
	// effect that reprojects + blends across frames (screen-space shadows, SSAO, RC GI, …) — they all
	// want the same thing: a ping-pong buffer holding an accumulated value + eye depth (for the
	// disocclusion test) plus last frame's view / view-proj for reprojection. The channel layout
	// depends on the value's width, so the format is a ctor arg: R16G16 for a scalar value (shadows,
	// AO: R = value, G = depth), RGBA16F for an RGB value (RC irradiance: RGB = value, A = depth).
	//
	// This is THE thing RenderGraph can't do for us: rg.CreateTexture resources are transient — the
	// graph recycles their memory every frame. Anything that must persist (a history/accumulation
	// buffer) is allocated OUTSIDE the graph as an RTHandle and IMPORTED each frame via
	// rg.ImportTexture. We keep TWO buffers and ping-pong: read last frame's (history), write this
	// frame's (which also becomes next frame's history).
	public class TemporalHistory
	{
		private readonly RTHandle[] _buffers = new RTHandle[2];
		private readonly string _name;
		private readonly GraphicsFormat _format;
		private int _writeIndex;
		private int _width, _height;

		public bool HasHistory { get; private set; }
		public Matrix4x4 PrevView { get; private set; }
		public Matrix4x4 PrevViewProj { get; private set; }

		// format defaults to the scalar R16G16 layout the shadow/AO resolves use; RC passes RGBA16F.
		public TemporalHistory(string name, GraphicsFormat format = GraphicsFormat.R16G16_SFloat)
		{
			_name = name;
			_format = format;
		}

		// Ensures correctly-sized buffers exist and returns this frame's imported read (history)
		// and write handles, plus the raw RTHandle of the history (bound directly on the material —
		// a persistent RT exists at record time, unlike a transient graph texture). A size change
		// or first use drops history so we don't reproject against a stale/garbage buffer.
		public void GetFrameTextures(RenderGraph rg, int width, int height,
			out TextureHandle history, out TextureHandle write, out RTHandle historyRt)
		{
			if (_buffers[0] == null || _width != width || _height != height)
			{
				Realloc(width, height);
				HasHistory = false;
			}

			int readIndex = 1 - _writeIndex;
			historyRt = _buffers[readIndex];
			history = rg.ImportTexture(_buffers[readIndex]);
			write = rg.ImportTexture(_buffers[_writeIndex]);
		}

		// Call after recording the resolve pass: this frame's matrices become next frame's "prev",
		// the buffers flip (what we just wrote is next frame's history), and history is now valid.
		public void Advance(Matrix4x4 view, Matrix4x4 viewProj)
		{
			PrevView = view;
			PrevViewProj = viewProj;
			_writeIndex = 1 - _writeIndex;
			HasHistory = true;
		}

		private void Realloc(int width, int height)
		{
			Release();
			_width = width;
			_height = height;
			for (int i = 0; i < 2; i++)
			{
				_buffers[i] = RTHandles.Alloc(width, height,
					colorFormat: _format,
					filterMode: FilterMode.Bilinear, // history is sampled at reprojected (sub-texel) UVs
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
