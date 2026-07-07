using System.Collections.Generic;

namespace TDRP
{
	// Per-camera wrapper around ColorHistory — the exact same rationale as TemporalHistoryCache: the
	// editor's Scene-view and Game-view cameras both render every repaint at different resolutions and
	// positions, and a single shared prev-color buffer would let them stomp each other (each frame would
	// gather from the OTHER camera's view, and the differing resolutions would realloc + drop history
	// constantly). Keying by camera.GetEntityId() isolates them, and also covers any real multi-camera
	// setup later. Quiet cameras (transient editor preview/thumbnail cams) are evicted to reclaim their
	// RTHandle pair instead of leaking one forever.
	public class ColorHistoryCache
	{
		private class Entry
		{
			public ColorHistory History;
			public int LastUsedFrame;
		}

		private const int EvictAfterFrames = 60;

		private readonly Dictionary<int, Entry> _entries = new();
		private readonly List<int> _stale = new();
		private readonly string _name;

		public ColorHistoryCache(string name)
		{
			_name = name;
		}

		// cameraId: camera.GetEntityId() (stable for the camera's lifetime). frame: the pipeline's
		// render counter (advances per render, editor-safe — NOT Time.frameCount).
		public ColorHistory Get(int cameraId, int frame)
		{
			Evict(frame);

			if (!_entries.TryGetValue(cameraId, out var entry))
			{
				entry = new Entry { History = new ColorHistory($"{_name}_{cameraId}") };
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
