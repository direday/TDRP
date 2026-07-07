# Distributing this package

Right now the package is **embedded** in the TDRP project (a plain folder under
`Packages/`, tracked in the TDRP repo). That makes TDRP the editable *dev host*.
To reuse it in other projects you publish it to a remote once, then consume it
per project. Two consumption models — pick per project, you can mix them.

Prereq: create an **empty** repo on GitHub/GitLab (no README) and note its URL.
`gh` is not installed here, so do this in the browser or install `gh` first.

## One-time: publish the package to its own repo

```bash
cd Packages/com.direday.tdrp
git init -b main
git add -A && git commit -m "Initial commit: TDRP pipeline v0.1.0"
git remote add origin <REMOTE_URL>
git push -u origin main
git tag v0.1.0 && git push --tags        # tag every release; consumers pin tags
cd ../..
```

## Model A — consume by git URL (recommended for *other* projects)

TDRP stays exactly as it is (embedded, editable source of truth). In a **new**
project, add to `Packages/manifest.json` → `dependencies`:

```json
"com.direday.tdrp": "<REMOTE_URL>#v0.1.0"
```

Read-only, resolved into the package cache. **Update** = bump the tag (or use the
Package Manager UI). Always pin a **tag**, never a branch (`#main`): Unity caches
the resolved commit in `packages-lock.json` and won't re-fetch a moving branch.

Release loop, run from the dev host:
1. iterate here, 2. bump `version` in `package.json` + add a `CHANGELOG` entry,
3. `git tag vX.Y.Z && git push --tags`, 4. bump the tag in each consumer.

## Model B — consume as a submodule (editable in that project too)

Use when you want to hack on the pipeline *from inside* a consuming project.

Convert **this** project's embedded copy to a submodule (after the publish step):

```bash
git rm -r --cached Packages/com.direday.tdrp
rm -rf Packages/com.direday.tdrp                 # content is safe on the remote
git submodule add <REMOTE_URL> Packages/com.direday.tdrp
git commit -m "Consume com.direday.tdrp as a submodule"
```

Add it to a **new** project the same way:

```bash
git submodule add <REMOTE_URL> Packages/com.direday.tdrp
```

Submodule gotchas:
- Clone consumers with `git clone --recurse-submodules` (or run
  `git submodule update --init` after cloning) — else the folder is empty and
  Unity errors "package has no package.json".
- **Before editing a submodule, `git checkout main`** inside it — a fresh
  submodule sits on a detached HEAD and edits committed there can be orphaned.
- Updating a consumer is manual: `cd Packages/com.direday.tdrp && git pull`,
  then `cd ../.. && git add Packages/com.direday.tdrp && git commit`.

## Per-project setup (both models)

The package ships only reusable code/assets. Each project supplies its own
config: **Assets → Create → TDRP → Create CustomRenderPipelineAsset**, assign it
under **Project Settings → Graphics**, and wire the shader/LUT references.
