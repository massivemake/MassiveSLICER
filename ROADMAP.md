# MassiveSLICER — Product Overview & Roadmap

> **Maintenance rule (humans and AI assistants):** this file is the FUTURE and the
> product definition; `memory.md` is the PAST (session changelog + build log).
> When you ship a feature: move it from Planned → Built here, and add a dated
> entry in `memory.md`. When priorities change: reorder the backlog here.
> AI sessions: this rule is repeated in `CLAUDE.md` / `AGENTS.md` so it loads
> automatically — follow it without being asked.

---

## What is this?

MassiveSLICER is Massive's in-house CAM application for **large-format additive
manufacturing (LFAM) with KUKA robot arms**. It turns 3D models into robot
motion programs (KRL) that drive pellet-extruder print heads — and previews,
validates, and monitors the result. It is a C#/.NET 8 desktop app (Avalonia UI,
OpenGL viewport) that runs on Windows and macOS, developed as the successor to
the Electron/JS prototype (`MassiveSlice`).

## What does it do today?

- **Slice** meshes into robot toolpaths: planar, angled, geodesic, and curved
  (sweep) strategies; adaptive layer heights; infill patterns; seam control
  with a visual seam editor; supports (TreeSupport / Formbound); X-bracing.
- **Decorate** toolpaths with surface effects: sine/sawtooth/triangle waves
  (Fixed and Dynamic phase methods), relief patterns with twist/fade, and
  live effector points that locally shape amplitude.
- **Export KRL** for KUKA KRC4: motion, temperatures, extruder RPM
  (`$ANOUT`), per-layer adaptive speed/flow, resume ramps, wipes.
- **Validate before printing**: per-move reachability + wrist-singularity IK
  analysis, TCP auto-rotation repair, loud warnings and an export gate.
- **Preview**: full toolpath rendering with bead (deposited material) view,
  playback with keyframes, robot pose simulation, per-move scrubbing.
- **Calibrate materials**: guided purge-and-weigh flow calibration per
  material preset, so exported RPM is computed from geometry, not guessed.
- **Connect to the cell live**: C3Bridge to the KRC4 (sync, program run),
  extruder/milling bridges (Live I/O), Zivid 3D scanning, rotary-bed
  calibration workflows.
- **Manage work**: `.mass` workspace files (models + toolpaths + settings),
  material and print presets, ERP hooks.

## What do we plan for it to do?

The north star: **an operator should be able to go from model to a successful
large-format print without folklore** — the app carries the knowledge.
The current frontier, in plain terms:

1. **No print dies from a preventable cause** — transfer verification,
   placement-accurate validation, calibrated flow from layer one.
2. **The bead is right everywhere, not just on average** — when the geometry
   demands wide and narrow beads in the same layer, speed and flow must adapt
   move-by-move so the surface doesn't come out bumpy or full of holes.
3. **Overhangs stop being the enemy** — use the robot's articulating head for
   **non-planar printing**: approach steep geometry from the side instead of
   always from the top, adapting the slicing strategy to the shape, so
   overhangs stop causing drips and failures.
4. **Clean starts and stops** — travel moves sequenced nearest-neighbor and
   as short as possible, so the head isn't dragging ooze ("poop") across the
   part between segments.
5. **Big parts as managed assemblies** — cut a large model into printable
   sections (adding structural bracing: X-bracing today, vertical
   bulkhead-style next), keep the master file as the source of truth, and
   export a per-section SRC with one click.
6. **What you see is what you get** — previews trustworthy enough to sign off
   surface quality before committing material and machine-days.
7. **Machine knowledge lives in presets** — materials, heads, cells, and their
   calibrations are data, not tribal memory.

## What problems are we trying to solve?

Real failures and costs that drove this work (see memory.md for histories):

| Problem | Cost when it happens | Countermeasure |
|---|---|---|
| Bead width variation without speed/flow adaptation | Bumpy surfaces where the bead is squeezed, holes where it's starved | Per-move geometry-adaptive speed & flow (planned, P2) |
| Overhangs printed planar (always from the top) | Drips, sagging, failed sections on steep geometry | Non-planar slicing using the articulating head (planned, P2) |
| Travel moves not sequenced nearest-neighbor | Start/stop drips ("poop") dragged across the part; wasted time | Travel-move optimizer — shortest possible travels (planned, P2) |
| Large parts printed monolithically | No structural bracing options; unwieldy programs | Model sectioning + bracing (X built; vertical/bulkhead planned) with per-section SRC export (planned, P3) |
| Truncated program transfer | The Jefre curtain died at layer 718/1047 | Transfer verification (planned, P1) |
| Wrong starting RPM per material | Bottom third of a print smeared while dialing live | Purge-and-weigh calibration (built) |
| Wrist singularity / unreachable poses | Mid-print robot fault | IK validation + TCP auto-rotation (built); placement-accurate validation (planned) |
| Misleading previews | Wave texture looked broken in-app but printed fine (and vice versa) | High-fidelity bead renderer (built); material shading + wave diagnostics (planned) |
| Wave texture drift ("resonance" bands) | Visible banding on the printed surface | Dynamic phase method (built); last-mile refinement (planned) |
| Slow, opaque operations | 20–30 s frozen loads, invisible slicing progress | Async load + real progress (built); format optimization (planned) |

## System architecture

```
src/
├── MassiveSlicer.App/       Avalonia UI shell — Views, ViewModels (MVVM),
│                            cell loading, console + local control bridge,
│                            calibration dialogs, status/progress UI
├── MassiveSlicer.Core/      No-UI domain: models (Toolpath, SliceSettings,
│                            MaterialPreset, AppPreferences), slicers
│                            (Planar/Angled/Geodesic/Curved), effects (Wave,
│                            Pattern, TreeSupport/Formbound, speed/ramp
│                            post-processors), IO (KRL export/import, .mass
│                            workspace, presets), kinematics, C3Bridge
├── MassiveSlicer.Viewport/  OpenGL (OpenTK) scene: mesh + toolpath + bead
│                            renderers, PBR materials, IK/FK solver (KR120),
│                            loaders (glTF/STL/STEP/…), camera
└── MassiveSlicer.Tests/     xUnit
```

Key facts:
- **Coordinates:** Z-up right-hand everywhere (KUKA/CAM convention).
- **Robot I/O:** KRL `.src` files; `$ANOUT[1..3]` = zone temps,
  `$ANOUT[4]` = extruder motor % (percent of drive max — 100% ≈ 100 RPM on
  our machines). Live link via C3Bridge TCP :7000.
- **Build identity:** auto-generated at compile time — build number = git
  commit count, shown as `build N · date · sha`; maps 1:1 to a commit.
- **Branches:** `master` = integration (Thom merges); Mac-side work lands on
  `main`; sync by merging `origin/master` into `main`.
- **Platforms:** Windows (`net8.0-windows`) and macOS (`net8.0`; Zivid SDK
  optional; `tools/make_macos_app.sh` makes the launcher bundle).

## Features

### Built ✅
- Slicing: Planar / Angled / Geodesic / Curved; adaptive layer height; infill;
  Normal + Surface modes; contour-offset control; simplification
- Seams: direction control, guides, visual seam editor, zig-zag
- Supports & structure: TreeSupport / Target Support Selections, Formbound,
  X-bracing
- Wave effects: sine/sawtooth/triangle; **Fixed + Dynamic** phase methods;
  gradient; stagger; pattern effects (relief, twist, fade); live effectors
- KRL export: temps + RPM via `$ANOUT`, per-layer adaptive speed & flow,
  resume ramps, wipes, per-move TCP yaw; KRL **import** (toolpath from .src)
- Robot validation: per-move reachability + singularity IK pass, TCP
  auto-rotation repair, inline alerts, export confirmation gate, go-to-issue
- Visualization: high-fidelity bead renderer (chord-error decimated, honest
  for multi-million-move wave paths), live bead color, overhang/orientation
  overlays, playback + keyframe lane, robot pose scrub
- Materials: presets with temps/density/cost, **purge-and-weigh flow
  calibration** (true-RPM inputs + drive scale), HV/HF dual flow rates
- Print presets card: search/filter/favorites/import-export
- Workspaces: `.mass` save/load with async load + byte-accurate progress,
  drag-and-drop, recent list; real 0–100% slice progress
- Cell integration: multi-cell configs, C3Bridge sync + program run,
  Live I/O phases 1–3 (robot / extruder / milling), Zivid scanning +
  rotary-bed calibration, local HTTP control bridge + MCP tools
- Rendering: PBR metallic-roughness + material inspector, themes, floating
  panel UI, auto build numbering, macOS port (app bundle + Dock icon)
- Diagnostics: SliceLogger, `tools/wave_analysis.py` phase-coherence measurement

### Planned 🔲 (prioritized backlog below)
- Per-move geometry-adaptive speed & flow (width variation → bumps/holes)
- Non-planar slicing using the articulating head (overhang from the side)
- Travel-move optimizer (nearest-neighbor sequencing, shortest travels)
- Model sectioning: cut a master model into printable parts, per-section SRC export
- Vertical / bulkhead bracing (X-bracing exists)
- Transfer verification on Export / Send to Robot
- Placement-accurate reachability validation workflow
- HV/HF head selector in the calibration section
- Per-height flow ramp / RPM step-tower calibration mode
- Finished-print preview (bead uses viewport lighting + material appearance)
- In-app wave-coherence diagnostics
- Dynamic wave last-mile (interpolated matching, loop-wrapped smoothing)
- Workspace load-time optimization (compact toolpath encoding)
- Spindle RPM display; PBR polish leftovers

---

# Prioritized backlog

## P1 — Print-failure prevention

### 1. Transfer verification on Export KRL / Send to Robot
**Why:** the Jefre curtain print died because the program transfer was
truncated mid-file (ended mid-line at Z 2154 of 3185) — the robot ran out of
code at layer 718 of 1047. Purely mechanical to prevent.
**What:** after writing the .src, read it back and verify byte count matches
and the file ends with `END`; refuse/warn on mismatch. Strongest on the
Send-to-Robot network path where the original transfer died.
**Size:** small.

### 2. Trustworthy reachability validation (placement workflow)
**Why:** validation flagged 29k "unreachable" moves at Z 1690–2809 on a
toolpath the robot physically printed through — because the model's in-app
position didn't match its real position on the bed. A validator that cries
wolf gets ignored.
**What:** make placement-for-validation explicit: verify cell + model
placement matches production before validating, so red/purple markers are
predictions, not noise.
**Size:** medium (workflow design more than code).

## P2 — Print quality & motion

### 3. Per-move geometry-adaptive speed & flow
**Why:** prints with serious bead-width variation come out bumpy where the
bead is over-fed and holey where it's starved — today's Adaptive Speed and
Flow only adapts per layer, not within a layer.
**What:** compute demanded bead cross-section per move (from local wall
width / geometry) and modulate robot speed and RPM move-by-move, within the
extruder's response limits; expose min/max clamps; visualize the speed/flow
field on the toolpath before printing.
**Size:** large (touches slicer, export, and preview).

### 4. Non-planar slicing (articulating head)
**Why:** we mostly print planar — always approaching from the top — so steep
overhangs sag and drip and are our main failure mode. The robot has an
articulating head we're barely using.
**What:** grow the existing angled/curved/geodesic foundations into a true
non-planar strategy: approach overhung geometry from the side, adapt layer
orientation to the local surface, with IK/collision validation along the way
(the validation + TCP-rotation infrastructure already exists). Goal:
eliminate overhang failures without support material.
**Size:** large (the flagship slicing investment).

### 5. Travel-move optimizer (nearest neighbor)
**Why:** when a layer has multiple segments/edges, the current sequencing
doesn't pick the closest next segment — long travels drag ooze ("poop")
across the part at every start/stop and waste time.
**What:** order segments per layer by nearest-neighbor (with seam and
direction constraints); invariant: the travel between two printed segments
is always as short as possible. Combine with wipes/resume ramps for clean
starts.
**Size:** medium.

## P3 — Structure & big-part workflow

### 6. Model sectioning with per-section SRC export
**Why:** parts bigger than one print need to be cut into sections — but the
cutting should not fork the design file into untracked copies.
**What:** cut a model inside the workspace (planes/boxes), keep the master
model + all sections in one `.mass`, slice sections individually, and export
"just this section" to SRC with one click.
**Size:** large.

### 7. Vertical / bulkhead bracing
**Why:** sectioned and thin-walled parts need internal structure; today only
X-bracing exists.
**What:** additional bracing generators — vertical walls / bulkhead-style
ribs — placeable per region, sliced integrally with the part.
**Size:** medium.

## P4 — Calibration & flow

### 8. HV/HF head selector in the calibration section
**Why:** presets carry two flow rates (HV / HF — one per extruder head); the
purge-and-weigh calculator writes only one, and the operator shouldn't have
to know which field by folklore.
**What:** head selector routing the computed flow rate to the right field,
recorded in the provenance note.
**Size:** small.

### 9. Per-height flow ramp / calibration print mode
**Why:** purge-and-weigh sets steady-state flow, but first layers behave
differently (heat-up, adhesion) — the curtain's bottom-third smear was flow
drift corrected live.
**What:** optional flow ramp by height and/or an RPM step-tower calibration
slice for empirical verification.
**Size:** medium.

## P5 — Visualization & verification

### 10. Finished-print preview (bead material shading)
**Why:** Show Bead exists to judge how the print will look — "Clear" should
preview translucent/glossy, not flat gray.
**What:** bead rendering honors viewport lighting/shader settings and the
material preset's appearance.
**Size:** medium.

### 11. In-app wave diagnostics
**Why:** `tools/wave_analysis.py` caught the Fixed-method drift and validated
Dynamic, but lives outside the app.
**What:** a "check wave coherence" pass after slicing (phase-drift overlay or
per-height table).
**Size:** medium.

### 12. Dynamic wave — last mile
**Why:** Dynamic halves worst-height phase error vs Fixed; deep folds still
show residual wobble.
**What:** interpolated parent-point matching + loop-wrapped smoothing; verify
on a physical test section.
**Size:** small–medium.

## P6 — Comfort & performance

### 13. Workspace load-time optimization
**Why:** multi-million-move projects take 20–30 s to open; the .mass format
is verbose JSON.
**What:** profile the parse; likely wins: compact toolpath encoding, lazy GPU
upload.
**Size:** medium–large.

### 14. Smaller carry-overs
- Spindle RPM display (KUKA `$ANOUT` polling or ATV340 Modbus).
- PBR polish (prefiltered-env IBL, alpha blend ordering, UV panel).
- Obsolete build-folder cleanup.
