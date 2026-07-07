# Top-Down Render Pipeline (`com.direday.tdrp`)

Custom Scriptable Render Pipeline built on the Unity 6 `RenderGraph` API.

## Layout

```
Runtime/    pipeline + passes + data  (asmdef: Direday.TDRP.Runtime, namespace TDRP)
Editor/     custom inspector          (asmdef: Direday.TDRP.Editor,  Editor-only)
Shaders/    .shader + Includes/*.hlsl
Textures/   SMAA lookup textures (+ .h provenance)
```

## Requirements

- Unity `6000.4+`
- `com.unity.render-pipelines.core` (declared dependency; pulls `Unity.Collections`)

## Install in a consumer project

**As a git submodule (editable, version-pinned per project):**

```bash
git submodule add <REMOTE_URL> Packages/com.direday.tdrp
```

**Or by git URL (read-only, resolved into the package cache):**

Add to `Packages/manifest.json` → `dependencies`:

```json
"com.direday.tdrp": "<REMOTE_URL>#v0.1.0"
```

> Always consume a **tag** (`#v0.1.0`), never a branch — Unity caches the
> resolved commit in `packages-lock.json` and won't re-fetch a moving branch.

## Per-project wiring (not shipped in the package)

The pipeline *code* is reusable; the *configured instance* and the graphics
wiring are per-project:

1. **Assets → Create → TDRP → Create CustomRenderPipelineAsset** — creates a
   `PipelineAsset` instance in your project's `Assets/`.
2. Assign it under **Project Settings → Graphics → Default Render Pipeline**
   (and in any Quality levels you use).
3. Give it the shader/LUT references (the create step wires the ones it can;
   confirm the SMAA lookup textures and compute shaders are assigned).

## Consuming the pipeline's types from your own scripts

`Direday.TDRP.Runtime` is `autoReferenced`, so project scripts (Assembly-CSharp)
can `using TDRP;` without extra setup. If you put your scripts in their own
asmdef, add `Direday.TDRP.Runtime` to its references.
