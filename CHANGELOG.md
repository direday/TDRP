# Changelog

All notable changes to this package are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Added
- DOTS instancing (BatchRendererGroup) support in `DefaultLit`, `DefaultUnlit` and
  `DefaultLitTransparent`: a `DOTS_INSTANCING_ON` variant per pass; per-instance
  `unity_ObjectToWorld` / `unity_WorldToObject` (required) and `unity_MatrixPreviousM/MI`
  (optional; per-object motion falls back to camera-only when absent); per-instance
  overrides of the material properties (`_BaseColor`, `_Metallic`, `_Smoothness`).
- `TDRP/RigidPartsLit`: DefaultLit for rigid-part-animated meshes. One mesh per unit; each
  vertex (stored part-local) carries its part index in UV channel 3 and is transformed by
  one part->world matrix from a global palette at `_PaletteBase + partIndex`. Per-part motion
  vectors from the previous palette. Contract in `Shaders/Includes/RigidParts.hlsl`.
  Per-instance `_TintColor` / `_FlashColor` overlays (RGB target, A amount) lerp each
  submesh from its own `_BaseColor`, so multi-material models keep their look under tinting.
- `PartMatrix` / `PartPalette` (runtime): the palette's 3x4 entry and a double-buffered owner
  that uploads it each frame and binds `_PartPalette`, `_PartPalettePrev`, `_PartPaletteHasPrev`.

### Changed
- `unity_MatrixPreviousM/MI` moved into the `UnityPerDraw` cbuffer (plus
  `unity_MotionVectorsParams`) as the SRP Batcher's motion-vector block.

## [0.1.0] - 2026-07-07

### Added
- Initial extraction of the Top-Down Render Pipeline from the TDRP sandbox
  project into a self-contained UPM package.
- `Direday.TDRP.Runtime` and `Direday.TDRP.Editor` assembly definitions.
