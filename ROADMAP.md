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
  with a visual seam editor; supports (TreeSupport / Formbound).
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
Concretely, that means closing the gaps where prints still fail or need
babysitting:

1. **No print dies from a preventable cause** — transfer verification,
   placement-accurate validation, calibrated flow from layer one.
2. **What you see is what you get** — the preview is trustworthy enough to
   sign off surface quality (bead material shading, wave-coherence checks)
   before committing material and machine-days.
3. **Machine knowledge lives in presets** — materials, heads, cells, and their
   calibrations are data, not tribal memory.

## What problems are we trying to solve?

Real failures and costs that drove this work (see memory.md for the full
histories):

| Problem | Cost when it happens | Countermeasure |
|---|---|---|
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
- Supports: TreeSupport / Target Support Selections, Formbound
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
- Transfer verification on Export / Send to Robot
- Placement-accurate reachability validation workflow
- HV/HF head selector in the calibration section
- Per-height flow ramp / RPM step-tower calibration mode
- Finished-print preview (bead uses viewport lighting + material appearance)
- In-app wave-coherence diagnostics
- Dynamic wave last-mile (interpolated matching, loop-wrapped smoothing)
- Workspace load-time optimization (compact toolpath encoding)
- Spindle RPM display (KUKA `$ANOUT` or ATV340 Modbus)
- PBR polish leftovers (prefiltered-env IBL, alpha blend ordering, UV panel)

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
placement matches production before validating (e.g., a "validate at this
placement" confirmation, or placement capture from the real cell), so
red/purple markers are predictions, not noise.
**Size:** medium (workflow design more than code).

## P2 — Calibration & flow

### 3. HV/HF head selector in the calibration section
**Why:** presets carry two flow rates (Flow rate HV / Flow rate HF — one per
extruder head). The purge-and-weigh calculator writes only one; the operator
must not have to know which field by folklore.
**What:** a head selector in the CALIBRATION section that routes the computed
flow rate to the right field and records which head was calibrated in the
provenance note.
**Size:** small.

### 4. Per-height flow ramp / calibration print mode
**Why:** purge-and-weigh sets the correct steady-state flow, but the first
layers behave differently (heat-up, bed adhesion). The curtain's bottom-third
smear was flow drift the operator corrected live.
**What:** optional flow ramp by height (start %, reach 100% by Z), and/or an
RPM step-tower calibration slice (bands labeled in KRL comments) for
empirical verification of the purge-derived constant.
**Size:** medium.

## P3 — Visualization & verification

### 5. Finished-print preview (bead material shading)
**Why:** the whole point of Show Bead is judging how the physical print will
look — a "Clear" material should preview translucent/glossy like the real
part, not flat gray.
**What:** bead rendering honors the Viewport lighting/shader settings
(exposure, IBL, backdrop) and the material preset's appearance.
**Size:** medium.

### 6. In-app wave diagnostics
**Why:** `tools/wave_analysis.py` (phase-coherence measurement) caught the
Fixed-method drift and validated Dynamic — but it lives outside the app and
requires exporting + running Python.
**What:** a "check wave coherence" pass in-app after slicing (phase-drift
overlay or per-height table), so texture problems are visible before printing.
**Size:** medium.

### 7. Dynamic wave — last mile
**Why:** Dynamic roughly halves worst-height phase error vs Fixed, but deep
folds still show residual wobble above the measurement noise floor.
**What:** two known levers not yet pulled: interpolated parent-point matching
(vs nearest-sample snapping) and loop-wrapped smoothing. Gate: verify on a
physical test section before/after.
**Size:** small–medium.

## P4 — Comfort & performance

### 8. Workspace load-time optimization
**Why:** large projects (multi-million-move toolpaths) take 20–30 s to open.
Progress reporting made it honest; it's still slow. The .mass format is
verbose JSON.
**What:** profile the parse; likely wins: binary/compact toolpath encoding in
the workspace, lazy toolpath GPU upload.
**Size:** medium–large.

### 9. Smaller carry-overs
- Spindle RPM display (needs KUKA `$ANOUT` polling or ATV340 Modbus).
- PBR polish (prefiltered-env IBL, alpha blend ordering, UV panel).
- Obsolete build-folder cleanup.
