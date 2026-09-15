# Import and slice

Import brings mesh/CAD into the scene; `slice` generates additive toolpaths asynchronously.

## Sub-features

- `import-mesh` imports STL/OBJ/3MF/GLB/GLTF (STEP via Cascadio when available).
- `slice` builds toolpaths (`RunSliceAsync` â†’ PlanarSlicer / other strategies).
- `prepare` / `preview` only switch AppMode (edit vs KRL playback) â€” they do not generate toolpaths.

## How to get to it (user POV)

- Console: `import`, `slice` (confirm `prepare`/`preview` in `help` as mode switches).
- Drag-drop onto the viewport; File import UI.

## Driving it with control-massiveslicer

Preconditions:

- `doctor` is ok.
- Tiny fixture: `assets/test/test_cube.stl` in the repo.
- Prefer `command new` first so the scene is empty.

- **Import.** Run `... command import "<absolute-path-to-test_cube.stl>"`. Expect `[import] Added â€¦` in console.
- **Slice.** Run `... command slice`. Poll `console -N 40` until `[progress] Slice complete` / failure text. HTTP `ok` alone does not mean slice finished.
- **Proof.** Screenshot `artifacts/import-and-slice/after-slice.png` plus console dump JSON.

## Gotchas

- Slice can take a while â€” use `test_cube.stl` for verifies.
- STEP needs Cascadio/Python tooling; it is not Windows-only in source.
- Never follow slice with live `move-*` robot playback in default recipes (see `ik-fk-scrub.md` for sim scrub).
- Use `import`, not `open`, for STL/OBJ — `open` is workspace (`.mass`); opening an STL fails with `[workspace] Failed to load`.
- A successful `import` often auto-starts slice (`[progress] Starting slice`). A second bare `slice` may say "Select a mesh first" if selection cleared.
- After planar post-processing, console may sit on `[collision] environment: … triangles` for a long time (LFAM 2 env ~243k tris). Bridge can stay up; earlier runs hung/crashed here — poll `scrub show` / wait before declaring failure. Toolpath may already be armed (e.g. scrub reported 72210 moves while collision line was still the last console entry).