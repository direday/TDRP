using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;

namespace TDRP
{
	// One palette entry: the affine PART -> WORLD transform as three rows (world = rows * (p, 1)).
	// Layout matches `PartMatrix` in Shaders/Includes/RigidParts.hlsl — 48 bytes, no padding.
	[StructLayout(LayoutKind.Sequential)]
	public struct PartMatrix
	{
		public Vector4 Row0;
		public Vector4 Row1;
		public Vector4 Row2;

		public static PartMatrix Identity => new(Matrix4x4.identity);

		// Drops the projective bottom row; part transforms are affine by contract.
		public PartMatrix(Matrix4x4 m)
		{
			Row0 = m.GetRow(0);
			Row1 = m.GetRow(1);
			Row2 = m.GetRow(2);
		}
	}

	// The global part-matrix palette read by TDRP/RigidPartsLit (contract: RigidParts.hlsl).
	//
	// Double-buffered for motion vectors: each Upload writes this frame's palette and keeps the
	// previous upload bound as _PartPalettePrev, so an animated part's motion is its own
	// frame-to-frame delta. Call Upload once per rendered frame — also when nothing moved,
	// otherwise the "previous" palette goes stale and still parts report motion.
	//
	// Palette slots are owned by the caller: each draw (renderer or BRG instance) sets
	// _PaletteBase to its first slot, and its mesh's part indices are relative to that. A slot
	// must keep meaning the same part across consecutive frames, or its motion vector is garbage;
	// after reassigning slots (spawn/despawn compaction, teleports) call InvalidateHistory.
	public sealed class PartPalette : IDisposable
	{
		private static readonly int _paletteId = Shader.PropertyToID("_PartPalette");
		private static readonly int _palettePrevId = Shader.PropertyToID("_PartPalettePrev");
		private static readonly int _hasPrevId = Shader.PropertyToID("_PartPaletteHasPrev");

		private static readonly int _stride = Marshal.SizeOf<PartMatrix>();

		private GraphicsBuffer _current;
		private GraphicsBuffer _previous;
		// Whether _current holds a real palette that may become next frame's history.
		private bool _currentIsValid;
		private bool _hasPrevious;

		public int Capacity { get; private set; }

		public PartPalette(int capacity)
		{
			Allocate(Mathf.Max(1, capacity));
			// Bind immediately so RigidPartsLit never samples an unbound buffer before the first Upload.
			Bind();
		}

		// Uploads matrices[0..count) as this frame's palette; the previous upload becomes the
		// motion-vector history. Grows the buffers if needed (dropping history for that frame).
		public void Upload(NativeArray<PartMatrix> matrices, int count)
		{
			if (count > matrices.Length)
				throw new ArgumentOutOfRangeException(nameof(count), "count exceeds the source array");

			if (count > Capacity)
			{
				Release();
				Allocate(Mathf.NextPowerOfTwo(count));
			}

			(_current, _previous) = (_previous, _current);
			_hasPrevious = _currentIsValid;
			_current.SetData(matrices, 0, 0, count);
			_currentIsValid = true;
			Bind();
		}

		// Next frame renders with no object motion from the palette (camera motion only).
		public void InvalidateHistory()
		{
			_currentIsValid = false;
			_hasPrevious = false;
			Bind();
		}

		public void Dispose()
		{
			Release();
		}

		private void Allocate(int capacity)
		{
			Capacity = capacity;
			_current = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, _stride) { name = "TDRP Part Palette A" };
			_previous = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, _stride) { name = "TDRP Part Palette B" };
			_currentIsValid = false;
			_hasPrevious = false;
		}

		private void Release()
		{
			_current?.Release();
			_previous?.Release();
			_current = null;
			_previous = null;
		}

		private void Bind()
		{
			Shader.SetGlobalBuffer(_paletteId, _current);
			// Always bound (to a valid buffer) even without history; the shader ignores it then.
			Shader.SetGlobalBuffer(_palettePrevId, _hasPrevious ? _previous : _current);
			Shader.SetGlobalFloat(_hasPrevId, _hasPrevious ? 1f : 0f);
		}
	}
}
