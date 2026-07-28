# Code map — read this instead of reading big files

**Why this file exists:** four of our largest files are 2,600–14,900 lines. Reading
`ViewportView.axaml.cs` end to end costs roughly 150k tokens and fills most of an AI context
window before any work starts. Use the anchors below to `grep` and read a few hundred lines
instead. Line numbers drift — treat them as "search near here", and confirm with `grep -n`.

## Where things live (by task)

| I need to change… | Go to |
|---|---|
| Slicing (planar layers, contours, seams, shells) | `Core/Slicing/PlanarSlicer.cs` |
| Angled / Multi-Planar slicing | `Core/Slicing/AngledPlanarSlicer.cs` |
| Formbound / Lightning / tree support | `Core/Slicing/Lightning/LightningPlanner.cs`, `Core/Slicing/TreeSupport/` |
| X-bracing | `Core/Slicing/Lightning/XBracingPlanner.cs` |
| 2×4 sleeve / structural support cards | `Core/Slicing/StructuralSupportPlanner.cs`, `Core/Models/StructuralSupportSpec.cs` |
| Brim | `Core/Slicing/BrimPlanner.cs` |
| Wave / pattern effects | `Core/Slicing/Effects/` |
| KRL output, ANOUT/URM, temps, RPM | `Core/IO/KrlExporter.cs`, `Core/IO/KrlAnout.cs` |
| A setting's plumbing | `Core/Models/SliceSettings.cs` → `Core/Models/AppPreferences.cs` → `App/ViewModels/AdditiveSettingsViewModel.cs` → `App/Views/RightPanelView.axaml` (+ the reslice watchlist in `ViewportView.axaml.cs`) |
| Cell/robot definitions (LFAM 1/2/3, bed, tools) | `assets/cells/<CELL>/*.json`, `Core/Models/CellConfig.cs` |
| Right-panel UI | `App/Views/RightPanelView.axaml` (3,357 lines — grep the section label) |
| Viewport overlay / HUD / pills | `App/Views/ViewportOverlayView.axaml` |
| Bead / toolpath rendering | `Viewport/Rendering/ToolpathRenderer.cs` |

## Inside `App/Views/ViewportView.axaml.cs` (14,856 lines)

Sections are marked with `// -- Name ---`. Grep the label rather than scrolling.

| Section | ~Line |
|---|---|
| GL lifecycle (`OnRender` — GL thread; read `_vm`, never `DataContext`) | 256 |
| TCP readout | 1644 |
| Tool helpers | 1722 |
| Workspace UI session restore | 1886 |
| Cell swap (LFAM 1/2/3 switching, content transfer) | 2011 |
| Navigation helpers | 2580 |
| Pointer input (picking, drag, editors) | 2629 |
| Drag and drop (model + `.mass`) | 3662 |
| **Slice** (`ComputeToolpathAsync`, post-processors, status) | 3703 |
| Layer preview | 5372 |
| Lay Flat / Drop to Plate | 5549 |
| LFAM tool TCP selection | 6394 |
| Gizmo mode switching | 6699 |
| Keyboard transform (G/R/S) | 6710 |
| Speed/RPM value tags | 10913 |
| TCP keyframes | 11010 |
| Scrub IK | 11260 |
| Gizmo drag | 13485 |
| KRL export | 13881 |

## Inside `App/ViewModels/ViewportViewModel.cs` (7,530 lines)

| Section | ~Line |
|---|---|
| Toolpath colors | 358 |
| Render request / backdrop / light / shader | 1190–1319 |
| Gizmo mode, selection readout, focus overlay | 1319–1379 |
| Scrubber markers (unreachable red, singularity purple) | 3548 |
| Lay Flat | 4865 |
| Seam guide editor | 4882 |
| Toolpath seam editing (re-seam in place) | 4961 |
| Curved boundary editor | 5131 |
| Outliner / user scene objects | grep `RegisterOutlinerItem` |

## Inside `Viewport/SceneRenderer.cs` (2,609 lines)

Off-screen FBOs 82 · shader sources 118 · world light 278 · public state 328 ·
public methods 875 · selection 1723 · shader mode 2217 · FBO management 2404.

## Traps that have cost real time

- **`OnRender` runs on the GL thread.** Read the cached `_vm` field, never Avalonia
  `DataContext` — that has caused launch crashes twice.
- **`SeamGuideRenderer` is a shared sphere-marker renderer** (curved boundaries, sequence
  waypoints). Changing its API breaks `SequencePathRenderer`. Seam guide columns live in
  `SeamGuideColumnRenderer`.
- **Toolpath `SceneNode`s carry no mesh** — AABB helpers find nothing for them. Derive
  toolpath bounds from layer moves.
- **Zig-zag seam is single-skin by design.** See `memory.md` 2026-07-27.
