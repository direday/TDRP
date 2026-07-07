using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;

namespace TDRP
{
	// Per-camera wrapper around TemporalHistory. Temporal accumulation reprojects each pixel against
	// the PREVIOUS frame OF THE SAME CAMERA (it stores that camera's prev view / view-proj). With a
	// single shared history, the editor's Scene-view and Game-view cameras — which both render every
	// repaint, at different resolutions and view matrices — stomp each other's buffer: every render
	// reprojects against the OTHER camera's frame, the disocclusion test rejects, and (because the
	// resolutions differ) the buffer reallocates and drops history outright. Accumulation collapses
	// to raw every frame, which reads as shimmer. Keying the history by camera fixes that, and also
	// covers any real multi-camera runtime setup later (split-screen, reflection cameras).
	//
	// The editor spawns transient preview / thumbnail cameras with their own ids, so we evict
	// histories that have gone quiet to reclaim their RTHandles instead of leaking one pair forever.
	public class TemporalHistoryCache
	{
		private class Entry
		{
			public TemporalHistory History;
			public int LastUsedFrame;
		}

		// Drop a camera's history after this many frames without use. Generous, so the active
		// Scene/Game views are never evicted between repaints; tight enough that transient preview
		// cameras are reclaimed promptly. (Frames here = render counter ticks, which advance per
		// camera render — not Time.frameCount.)
		private const int EvictAfterFrames = 60;

		private readonly Dictionary<int, Entry> _entries = new();
		private readonly List<int> _stale = new();
		private readonly string _name;
		private readonly GraphicsFormat _format;

		// format is forwarded to every per-camera TemporalHistory (default R16G16 scalar; RC uses RGBA16F).
		public TemporalHistoryCache(string name, GraphicsFormat format = GraphicsFormat.R16G16_SFloat)
		{
			_name = name;
			_format = format;
		}

		// Returns this camera's history (created on first use) and reclaims any cameras that have
		// gone quiet. cameraId: camera.GetEntityId() (stable for the camera's lifetime). frame: an
		// editor-safe counter that advances on every render — NOT Time.frameCount, which freezes on
		// editor one-shot renders and would stop eviction (and the jitter) from ever advancing.
		public TemporalHistory Get(int cameraId, int frame)
		{
			Evict(frame);

			if (!_entries.TryGetValue(cameraId, out var entry))
			{
				entry = new Entry { History = new TemporalHistory($"{_name}_{cameraId}", _format) };
				_entries[cameraId] = entry;
			}
			entry.LastUsedFrame = frame;
			return entry.History;
		}

		private void Evict(int frame)
		{
			_stale.Clear();
			foreach (var kvp in _entries)
			{
				if (frame - kvp.Value.LastUsedFrame > EvictAfterFrames)
					_stale.Add(kvp.Key);
			}

			foreach (int key in _stale)
			{
				_entries[key].History.Release();
				_entries.Remove(key);
			}
		}

		public void Dispose()
		{
			foreach (var entry in _entries.Values)
				entry.History.Release();
			_entries.Clear();
		}
	}
}
