# MassiveSLICER Build Log

Build numbers appear in the status bar (bottom center). Numbering began mid-development
during the Jefre curtain project debugging; builds 1–8 are reconstructed from the
session that introduced them.

## Builds 1–8 — macOS crash hunt (Sine wave + Show bead)
- Added `SliceLogger`: timestamped slice-pipeline diagnostics written to
  `~/Desktop/massiveslicer-slice.log`, with per-stage timings.
- Diagnosed and fixed GPU memory crash: bead rendering allocated ~390 MB for
  wave-expanded toolpaths (500k+ moves × 36 verts); introduced bead decimation
  (MaxBeadSegments) to cap the upload.
- Build 8: wrapped the GL upload path in a logging try-catch — unhandled managed
  exceptions had been escaping to the native render loop and aborting the process
  (SIGABRT); now they log the real exception and show an error instead.

## Build 9 — bead crash root cause
- Fixed `IndexOutOfRangeException` in bead upload: decimation counted selected
  moves globally but the selection loop reset per layer, so the vertex array was
  undersized by one bead per layer.

## Builds 10–14 — bead rendering iterations
- 10: merged decimated moves into continuous beads (no gaps between segments).
- 11–12: experimented with rendering beads from the raw (pre-wave) toolpath;
  reverted — bead must show the wave geometry.
- 13: bead fallback color white instead of hardcoded blue.
- 14: beads render the wave toolpath; segment budget raised to 160k.

## Build 15 — the blue color mystery, solved
- Toolpath/bead color was coming from three overriding sources: saved
  `prefs.json` (stored blue from an old session), the material preset color
  mapping (fallback blue), and hardcoded defaults. All defaults now white;
  material fallback white.

## Build 16 — live bead color
- "Bead color" picker next to Show Bead: applied per-frame as a shader uniform,
  so it recolors already-sliced beads instantly (no re-slice), and persists.

## Builds 17–18 — wave phase lock experiments
- Attempted seam-drift fixes for the sine wave (fixed world anchor, then
  direction-aware chained anchor). Measurement showed no improvement — these
  led to the correct diagnosis and the Fixed/Dynamic design in build 21.
- Added `tools/wave_analysis.py`: measures wave phase coherence in exported
  .src files (direction-aware probes, stagger-subtracted residuals).

## Build 19 — high-fidelity bead renderer
- Rebuilt bead mesh as indexed geometry with chord-error decimation (points
  dropped only where the path is straight within 0.35 mm). Full 2.7M-move wave
  toolpaths now render faithfully — the old fixed-step decimation cut chords
  across entire wavelengths and misrepresented the wave.
- Overhang/orientation overlays share the bead index buffer (also fixed a
  latent ~2.3 GB allocation on wave toolpaths).

## Build 20 — scrub repaint fix
- Viewport froze when scrubbing through robot-unreachable poses (repaint only
  fired on successful IK solves). Scrubbing now always repaints.

## Builds 21–22 — Fixed / Dynamic wave phase methods
- New "Phase" dropdown in wave settings:
  - **Fixed** — original seam-anchored behaviour, byte-identical output
    (verified against the printed curtain program).
  - **Dynamic** — phase inheritance: each layer continues the wave of the layer
    directly below plus stagger, eliminating the layer-to-layer drift that
    produced moiré texture bands ("resonance") on morphing cross-sections.
- 22: robust Dynamic fitting (median filter, symmetric slope clamp) after
  measurement showed one-directional lag in deep folds.

## Builds 23–24 — saving, progress, and load
- Fixed doubled extensions from save dialogs (`.src.src`, `.mass.mass`) across
  all five save flows (workspace, KRL, Send to Robot, PLY, STL).
- Workspace loading moved off the UI thread (no more 20–30 s freeze).
- Real 0–100 % progress with stage text: byte-accurate file-read progress on
  project open; per-layer progress during planar slicing.
- macOS: `MassiveSlicer.app` bundle generator (`tools/make_macos_app.sh`) and
  runtime Dock icon, so Cmd+Tab shows the Massive logo and app name.

## Build 25 — material flow calibration (purge & weigh)
- Material Preset dialog gains a guided CALIBRATION section: run the extruder
  at a known motor % for a known time, weigh the purge, enter three numbers —
  the per-material flow rate (rev/cm³) is computed and applied with provenance
  (date + conditions). Fixes wrong starting RPM from the uncalibrated default
  constant (the flow creep that smeared the bottom of the curtain print).

## Builds 26–28 — singularity defense (curtain print failure)
- 26: loud robot-validation summary after every slice (singularity/unreachable
  counts + height range) and a confirmation gate on Export KRL / Send to Robot.
- 27: **TCP auto-rotation repair** — for flagged spans, searches the smallest
  print-neutral nozzle spin that steers the wrist clear of singularity, ramps
  it in/out smoothly, re-verifies with IK, and bakes per-move yaw into the KRL
  export. Also fixed the exporter's gimbal-lock handling which silently zeroed
  any TCP rotation for a straight-down tool — the root cause of "the slicer
  never rotates the TCP".
- 28: "Go to first issue" jumps the timeline to the first flagged move so the
  robot preview shows the failing pose.

## Builds 29–30 — inline status UI (no popups)
- Progress and alerts moved into the bottom status bar: busy strip with a
  determinate progress bar + stage text, and a red ⚠ validation alert row with
  the Go-to-issue button. Export KRL keeps its confirmation dialog, now with
  Cancel / Go to issue / Export anyway.
- Status bar shows the loaded workspace filename instead of "No file loaded".
- 30: repeatable wording in the export warning; timeline markers preserved
  after Go-to-issue.

## 2026-07-16 — URM output fix (OUT[8]) + calibrated travel defaults
- Export-to-Robot (Digital Start/Stop URM): pulses now emit on `$OUT[8]` (verified URM input on
  the LFAM cells) and the robot-mode gate `$OUT[9]` is latched TRUE in MAT — previous builds
  pulsed the gate itself (Caracol slide numbering), which froze extruder setpoints at 0.
- New-project defaults set to the calibrated T5 travel recipe: 600 mm/s travel + wipe,
  Same-Direction wipe 12 mm, ramp 4 mm, z-hop 3 mm, resume pause 0.5 s.
