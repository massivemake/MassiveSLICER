# MassiveSLICER Roadmap

What we're building next and why, in priority order. The past (shipped work,
session logs, build history) lives in `memory.md`. Update this file when
priorities shift or items ship — move shipped items to a memory.md entry.

Build numbers are auto-generated (git commit count, `build N · date · sha` in
the status bar), so items here get associated with the builds that ship them
via `git log`.

---

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
**Why:** presets now carry two flow rates (Flow rate HV / Flow rate HF — one
per extruder head). The purge-and-weigh calculator writes only one; the
operator must not have to know which field by folklore.
**What:** a head selector in the CALIBRATION section that routes the computed
flow rate to the right field, and records which head was calibrated in the
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

### 9. Carry-overs from memory.md "Pending"
- PBR polish leftovers (prefiltered-env IBL, alpha blend ordering, UV panel).
- Spindle RPM display (needs KUKA $ANOUT or ATV340 Modbus).
- Obsolete build-folder cleanup.
