# Viewport overlays and display settings

Left-panel viewport visibility toggles and per-view display profiles (Grid, Axes, Bed grid, TCP axes, shader, etc.). Separate from camera framing (`viewport-camera.md`).

## Sub-features

- `show-grid` — ground-plane grid (`ShowGrid`).
- `show-axes` — world axes (`ShowAxes`).
- `show-bed-grid` — print-bed boundary/grid overlay (`ShowBedGrid` → `SceneRenderer` / `BedBoundaryRenderer`).
- `show-tcp-frame` — TCP axes (`ShowTcpFrame`).
- `view-display-profiles` — Body / Toolpath / Speed / RPM / Thermal / Preview (and Edit) each keep their own `ViewDisplayProfile`.
- `viewmode-bridge` — console `viewmode <Body|Toolpath|…>` applies the profile.
- `viewset-bridge` — console `viewset <Property> [value]` for bool/float/int ViewportViewModel props.

## How to get to it (user POV)

- Left panel → Viewport settings → VISIBILITY (Grid / Axes / Bed grid / TCP axes).
- View-mode pills (Body / Toolpath / …) swap the saved profile.
- Shader control (Standard / Clay / Arctic / MatteBlack / …).

## Driving it with control-massiveslicer

Preconditions: `doctor` ok; prefer `connected: false`.

1. `command viewmode Body` (or Toolpath).
2. `command viewset ShowBedGrid` / `viewset ShowBedGrid true` (bools work).
3. `command viewset ActiveShaderMode` (read). Note: setting enum values via `viewset` currently fails (string→ShaderMode); switch shader in UI or via viewmode profile that stores a parseable name.
4. Screenshot under `artifacts/viewport-overlays/`.

Hard bans: do not use live `sync` / `move-*` for overlay proofs.

## Gotchas

- **Arctic (PR #7 / heated-bed branch):** print-area **bed grid** still draws when `ShowBedGrid` is on (`BedBoundaryOverlay.ShouldDrawOverlay` = `showBedGrid && !slicePlaneViewerActive`; SceneRenderer draws bed overlay in Arctic). **Ground** grid (`ShowGrid`) remains suppressed in Arctic (`!arcticPresentation`). Older live note (2026-09-15 pre-PR) that Arctic hid bed grid is stale for builds with that fix.
- Toolpath / Speed / RPM / Thermal / Edit default profiles set `ShowBedGrid=false` (and often dark + MatteBlack). Turning Bed grid on saves into that view's profile.
- Also hidden when `SlicePlaneViewerActive` or `_bedBoundary` is null.
- `ShowGrid` ≠ `ShowBedGrid` (ground plane vs print-bed boundary).
- `viewset` only parses double/float/int/bool; enums need a product `Enum.TryParse` (harness gap until fixed).

LFAM 3 heated vs rotary meshes and BASE ghosting: see [lfam3-dual-bed.md](./lfam3-dual-bed.md) (shop Release lfam3.json + Hermes memory).
