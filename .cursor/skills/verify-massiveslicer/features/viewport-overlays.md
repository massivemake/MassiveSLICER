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

- **Arctic suppresses bed grid and ground grid.** `SceneRenderer` only draws them when `!arcticPresentation` (`ActiveShaderMode == Arctic`). Checkbox can be ON while overlay is hidden — live 2026-09-15: Preview/Body with Arctic + `ShowBedGrid=true` showed no bed grid; Toolpath (`MatteBlack`) + `ShowBedGrid=true` draws it.
- Toolpath / Speed / RPM / Thermal / Edit default profiles set `ShowBedGrid=false` (and often dark + MatteBlack). Turning Bed grid on saves into that view's profile.
- Also hidden when `SlicePlaneViewerActive` or `_bedBoundary` is null.
- `ShowGrid` ≠ `ShowBedGrid` (ground plane vs print-bed boundary).
- `viewset` only parses double/float/int/bool; enums need a product `Enum.TryParse` (harness gap until fixed).
