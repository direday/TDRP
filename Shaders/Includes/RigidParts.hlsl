#ifndef TDRP_RIGID_PARTS_INCLUDED
#define TDRP_RIGID_PARTS_INCLUDED

// Rigid-part ("one bone per vertex") animation for hard-surface meshes: a whole unit is ONE
// mesh, each vertex tagged with the index of the part it belongs to, and every part's current
// transform lives in a global palette of 3x4 matrices that the CPU rewrites each frame.
//
// Contract (mirrored by TDRP.PartPalette on the C# side):
//   - Vertex positions/normals are stored in PART-LOCAL space.
//   - Palette entries are PART -> WORLD (the unit transform already composed in), so the
//     renderer's own object matrix is not used for positions. Part scale must be uniform:
//     normals go through the same 3x3 and are renormalised.
//   - Vertex part index: TEXCOORD3.x (UV channel 3), a float holding a small integer.
//   - The draw's palette offset: _PaletteBase (material property; per-instance under BRG).
//     Entry = _PaletteBase + partIndex.
//   - _PartPalettePrev holds last frame's palette for motion vectors; while
//     _PartPaletteHasPrev == 0 it is ignored and the current palette is used (no object motion).

struct PartMatrix
{
	// Rows of the affine part->world transform: world = float3(dot(row0, p), dot(row1, p), dot(row2, p)).
	float4 row0;
	float4 row1;
	float4 row2;
};

StructuredBuffer<PartMatrix> _PartPalette;
StructuredBuffer<PartMatrix> _PartPalettePrev;
float _PartPaletteHasPrev;

// _PaletteBase must be declared (and, under DOTS, remapped) by the including shader first.
uint GetPartPaletteIndex(float partIndex)
{
	return (uint)(_PaletteBase + 0.5) + (uint)(partIndex + 0.5);
}

float3x4 ToFloat3x4(PartMatrix m)
{
	return float3x4(m.row0, m.row1, m.row2);
}

float3x4 LoadPartMatrix(uint index)
{
	return ToFloat3x4(_PartPalette[index]);
}

float3x4 LoadPrevPartMatrix(uint index)
{
	return _PartPaletteHasPrev != 0.0 ? ToFloat3x4(_PartPalettePrev[index]) : LoadPartMatrix(index);
}

float3 PartToWorld(float3x4 m, float3 positionPS)
{
	return mul(m, float4(positionPS, 1.0));
}

float3 PartToWorldNormal(float3x4 m, float3 normalPS)
{
	// Rigid + uniform scale: the linear part is a scaled rotation, so no inverse-transpose.
	return normalize(mul((float3x3)m, normalPS));
}

#endif
