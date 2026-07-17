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
5. **Big parts as managed assemblies** — the bed is 10'×10' (or 10'×20'), the
   art is bigger. One master model in the slicer; cutting planes act as
   cuts/masks producing **compositions** (sections) that live beside the
   master in the same file. Effects (sine wave) and bracing are set on the
   master so every printed section lines up when assembled. Each composition
   slices and exports its own SRC with one click. Bracing is a **stackable
   modifier system** (X / lattice for stiffness, vertical braces — including
   a beam-sleeve end for mounting), and **assembly joint modifiers** at cut
   points (flanges, lips, brackets — e.g., a taper that seats one section
   inside the next, with insertion depth height-compensated so the assembled
   stack matches the master's height).
6. **Every print teaches the next one** — exported programs carry provenance
   metadata that ties each print to its Massive Lab (ERP) job, so logs, notes,
   and outcomes attach to real records instead of "print #1920". That dataset
   feeds failure analysis and, over time, a recommendation engine: suggested
   presets and settings from the project description and the part's geometry.
7. **What you see is what you get** — previews trustworthy enough to sign off
   surface quality before committing material and machine-days.
8. **Machine knowledge lives in presets** — materials, heads, cells, and their
   calibrations are data, not tribal memory.

## What problems are we trying to solve?

Real failures and costs that drove this work (see memory.md for histories):

| Problem | Cost when it happens | Countermeasure |
|---|---|---|
| Bead width variation without speed/flow adaptation | Bumpy surfaces where the bead is squeezed, holes where it's starved | Per-move geometry-adaptive speed & flow (planned, P2) |
| Overhangs printed planar (always from the top) | Drips, sagging, failed sections on steep geometry | Non-planar slicing using the articulating head (planned, P2) |
| Travel moves not sequenced nearest-neighbor | The head sometimes crosses the **entire print** between segments, oozing ("pooping") the whole way; wasted time | Travel-move optimizer — shortest possible travels (planned, P2) |
| Parts bigger than the bed (10'×10' / 10'×20') | Ad-hoc cutting forks the design file; sections drift out of alignment; pieces don't register or join cleanly | Cut compositions + stackable bracing modifiers + assembly joint modifiers with height compensation (planned, P3) |
| Prints are anonymous ("print #1920") | Logs/notes/outcomes can't be tied to a print; no dataset to learn from, so failures repeat | KRL provenance metadata + Massive Lab job linkage → learning/recommendation engine (planned, P4) |
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
- Cut compositions: cutting planes on a master model → per-section slicing + SRC
- Bracing as stackable modifiers: X (built) + lattice + vertical + beam-sleeve ends
- Assembly joint modifiers at cut points: flange/lip/bracket, height-compensated
- Print provenance metadata in exported KRL + Massive Lab job linkage
- Preset / settings recommendation engine (project description + geometry)
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
**Why:** today's sequencing does NOT pick the closest next segment — the head
sometimes travels across the entire print between an edge and the next,
oozing ("pooping") over finished work the whole way, at every start/stop.
**What:** order segments per layer nearest-neighbor (with seam and direction
constraints). Target state: the travel between two printed segments is always
the shortest possible — this is the acceptance test, not the current
behavior. Combine with wipes/resume ramps for clean starts.
**Size:** medium.

## P3 — Structure & big-part workflow

### 6. Cut compositions (master model → per-section SRC)
**Why:** the bed caps prints at 10'×10' / 10'×20'; larger work must be cut.
Today that means forking the model into separate files — losing the single
source of truth, and losing continuity of effects across pieces (a sine wave
or bracing set per-piece won't line up after assembly).
**What:** cutting planes on the master act as cuts/masks, each producing a
**composition** — a section of the model that lives beside the master in the
same `.mass`. Effects and bracing are defined once on the master and carry
through every composition, so printed sections align at the joints. Each
composition slices independently and exports its own SRC with one click.
**Size:** large (in progress — cutting methodology being designed now).

### 7. Bracing as stackable modifiers
**Why:** X-bracing is built (`XBracingEnabled`) but is a single on/off — real
parts need combinable structure: stiffening against warp/bend AND mounting.
**What:** bracing becomes a **modifier stack** — multiple braces on one model:
- **X / lattice** — structural, keeps the model from warping or bending;
- **Vertical** — bulkhead-style ribs;
- **Beam sleeve** — a vertical brace terminating in a square/rectangular
  collar that wraps around a beam, for structural support and for mounting
  the finished print.
Placeable per region, sliced integrally with the part, stacking freely
(e.g., lattice for stiffness + a sleeved vertical for mounting).
**Size:** medium–large.

### 8. Assembly joint modifiers (flanges, lips, brackets)
**Why:** cut sections must register and join cleanly when stacked — right
now nothing shapes the mating surfaces, so fit-up is manual.
**What:** functional toolpath modifiers applied at cut points, by type:
**flange / lip / bracket** — e.g., a taper flange that narrows the bottom of
the upper section so it seats down inside the lower one. Critically,
**insertion depth is height-compensated**: how far a section sinks into its
neighbor is fed back into the composition heights so the assembled stack
still matches the master model's overall height.
**Size:** medium–large.

### Design coupling note (6 + 7 + 8)
These three are one workflow: cut the master into compositions → braces and
effects flow through from the master → joints shape the cut faces → per-
section SRC. The Dynamic wave phase method matters here: wave continuity
across section boundaries is what makes assembled surfaces read as one.

## P4 — Traceability & learning (Massive Lab)

### 9. Print provenance metadata in exported KRL
**Why:** a print today is anonymous — "print #1920" — so machine logs, operator
notes, and outcomes have nothing to attach to, and every failure's lessons
evaporate. The ERP link already exists (`docs/ERP-SlicerAPI.md`,
lab.massivemake.com project/element search is live in the slicer).
**What:** a provenance header in every exported .src (KRL comments): Massive
Lab project/element/job ID, workspace file, build `N · sha`, material preset +
calibration provenance, key slice settings (method, bead, layer, wave, flow).
Register the export with Massive Lab at export time so the job record exists
before the print starts; logs and notes then attach to it.
**Size:** medium (format + export hook are small; the ERP-side job record is
the coordination work).

### 10. Preset & settings recommendation engine
**Why:** with prints linked to outcomes (item 9), the data can start working:
minimize repeat failures and stop settings knowledge living in heads.
**What:** staged — (a) collect: settings + geometry features (size, wall
widths, overhang stats) + outcome per job; (b) recall: "similar past prints
and what worked" surfaced when slicing; (c) recommend: suggested preset/
settings from the project description and part geometry. Reinforcement-
learning-ready dataset from day one (state = settings+geometry, reward =
outcome).
**Size:** large (multi-stage, ERP-coupled; stage (a) is the near-term win).

## P5 — Calibration & flow

### 11. HV/HF head selector in the calibration section
**Why:** presets carry two flow rates (HV / HF — one per extruder head); the
purge-and-weigh calculator writes only one, and the operator shouldn't have
to know which field by folklore.
**What:** head selector routing the computed flow rate to the right field,
recorded in the provenance note.
**Size:** small.

### 12. Per-height flow ramp / calibration print mode
**Why:** purge-and-weigh sets steady-state flow, but first layers behave
differently (heat-up, adhesion) — the curtain's bottom-third smear was flow
drift corrected live.
**What:** optional flow ramp by height and/or an RPM step-tower calibration
slice for empirical verification.
**Size:** medium.

## P6 — Visualization & verification

### 13. Finished-print preview (bead material shading)
**Why:** Show Bead exists to judge how the print will look — "Clear" should
preview translucent/glossy, not flat gray.
**What:** bead rendering honors viewport lighting/shader settings and the
material preset's appearance.
**Size:** medium.

### 14. In-app wave diagnostics
**Why:** `tools/wave_analysis.py` caught the Fixed-method drift and validated
Dynamic, but lives outside the app.
**What:** a "check wave coherence" pass after slicing (phase-drift overlay or
per-height table).
**Size:** medium.

### 15. Dynamic wave — last mile
**Why:** Dynamic halves worst-height phase error vs Fixed; deep folds still
show residual wobble.
**What:** interpolated parent-point matching + loop-wrapped smoothing; verify
on a physical test section.
**Size:** small–medium.

## P7 — Comfort & performance

### 16. Workspace load-time optimization
**Why:** multi-million-move projects take 20–30 s to open; the .mass format
is verbose JSON.
**What:** profile the parse; likely wins: compact toolpath encoding, lazy GPU
upload.
**Size:** medium–large.

### 17. Smaller carry-overs
- Spindle RPM display (KUKA `$ANOUT` polling or ATV340 Modbus).
- PBR polish (prefiltered-env IBL, alpha blend ordering, UV panel).
- Obsolete build-folder cleanup.
