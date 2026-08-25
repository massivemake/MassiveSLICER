# Start and Stop Calibration Effort V1

**Goal:** Find the cleanest Start/Stop (travel) settings for Caracol LFAM cells in MassiveSLICER — wipe, z-hop, resume wait, travel speed — with a short dual-wall geometry that produces many island-to-island travels quickly.

**Workspace generator:** `StartStopCalibrationWorkspace` in Core  
**Default open path:**  
`~/Library/Application Support/MassiveSlicer/StartStopCalibration/Start_and_Stop_Calibration_Effort_V1.mass`

**Reference:** Caracol *Handover Start and Stop – V2* (2025-05-23)  
`/Volumes/MassiveFILES/Correspondence/_RECEIVED/2025_0523 - Start and Stop/Handover Start and Stop - V2.pdf`

---

## Test shape — 8-cell one-shot grid

Default workspace is a **2×4 grid of dual-wall pairs** printed **serially** (T1 complete → T2 → … → T8) so you can score eight wipe/z-hop recipes in one job.

| Dim | Default | Why |
|-----|---------|-----|
| Walls | 50 × 8 × 20 mm, gap 40 mm | Short tower, clear A↔B travel |
| Layers/cell | 8 @ 2.5 mm | Fast series |
| Grid | BASE (600,600) origin, pitch 130×150 mm | Reachable mid-bed |
| Order | T1…T4 bottom, T5…T8 top | See `TEST_MATRIX.txt` beside the `.mass` |

```
  T5   T6   T7   T8
  T1   T2   T3   T4
```

| Cell | Settings |
|------|----------|
| **T1** | No wipe · hop 0 · **resume 0 ms** |
| **T2** | No wipe · hop 3 · **resume 500 ms** |
| **T3** | Wipe 6 · hop 3 · **resume 0 ms** |
| **T4** | Wipe 12 · hop 3 · **resume 250 ms** |
| **T5** | Wipe 12 · hop 3 · **resume 500 ms** |
| **T6** | Wipe 12 · hop 3 · **resume 1000 ms** |
| **T7** | Retrace 12 · hop 3 · **resume 500 ms** |
| **T8** | Wipe 12 · hop 6 · **resume 500 ms** |

**Do not re-slice** — wipe/z-hop/**resume ms** are baked per cell. Export as-is.

**Digital Start/Stop (URM)** is enabled on this workspace (`4 TOOLPATH → PRINT TOOLPATH` checkbox). Export pulses `$OUT[9]`/`$OUT[7]` around travels when that box is checked.

---

## Baseline settings (Caracol V2 + MassiveSLICER mapping)

| Parameter | V1 baseline | Caracol note |
|-----------|-------------|--------------|
| Bead width | 6 mm | Eidos demo |
| Layer height | 2.5 mm | Eidos demo |
| Print speed | 30 mm/s | Eidos demo |
| Travel speed | **600 mm/s** | Suggested |
| Wipe speed | **600 mm/s** | Suggested |
| Wipe mode | Same-Direction | Path continues forward before hop |
| Wipe length | 8 mm | ≥ nozzle / bead diameter |
| Wipe ramp | 4 mm | Trailing RPM pull-down |
| Z-hop | 3 mm | Retraction lift |
| Resume wait | **0.5 s** | Caracol Code Editor default after `$OUT[7]=TRUE` |
| Start wait | 2 s | First purge settle (not in Caracol travel table) |
| Infill | None | Shell-only |

### Iteration knobs (what to sweep)

1. **`ExtrusionResumeWaitSec`** — 0 / 0.3 / 0.5 / 1.0 / 1.5 / 2.0  
   Too low → starve / voids at restart. Too high → blob / over-extrude.
2. **`WipeLengthMm` + mode** — Off vs Same-Direction vs Retrace; lengths 6–25 mm  
   Look at stringing and seam cleanliness at travel start.
3. **`ZHopMm`** — 0 / 3 / 5 / 8  
   Too low → drag. Too high → longer drips in air.
4. **`TravelSpeed` / wipe speed** — 300–600 mm/s  
   Faster travel = less drool time if screw is fully off.
5. **`WipeRampMm`** — shop **−1**: dip 1 mm −Z into the bead (RPM 0), then wipe length. Positive still ramps RPM on the last N mm of wipe.

Score each print at the **stop seam** (end of wall A) and **start seam** (begin of wall B) on every few layers.

---

## URM / Ultra-responsive mode — are we using it?

### Caracol full S&S (Code Editor inject)

Around every travel:

1. `$OUT[9] = TRUE` (URM) ~5000 ms of path before stop  
2. `$OUT[7] = FALSE` (screw / print enable) xxx ms before stop  
3. Slow robot ~50%, `WAIT SEC 0.5`  
4. Wipe → z-hop → travel  
5. `$OUT[7] = TRUE`, `WAIT SEC xxx`, then `$OUT[9] = FALSE`

Their post-processed sample (`travel_ss-post.txt`) has **~1276 per-travel pulses** of both OUT[7] and OUT[9].

### MassiveSLICER today

| Signal | Live IO label | What the exporter does |
|--------|---------------|-------------------------|
| `$OUT[9]` | **MIO request** | `TRUE` once in header (`;FOLD MAT`), `FALSE` only in footer — **held ON for the whole job** |
| `$OUT[7]` | **Print enable** | `TRUE` at READYTOPRINT, `FALSE` only in footer — **not pulsed per travel** |
| `$ANOUT[4]` | Extruder RPM | Set to **0** on travels (`TravelSetAnout4Zero`); wipe also forces extruder off; resume uses wait + RPM-on |

So:

- The cabinet pin for URM/MIO **is still driven** at job start (`$OUT[9]=TRUE`).
- We are **not** doing Caracol-style **per-travel** URM on/off, and we do **not** drop `$OUT[7]` on travels (we only zero analog RPM).
- Whether the PLC/extruder still implements “ultra responsive mode” when `$OUT[9]` is asserted is a **cabinet/control-code** question — software still asserts the bit for the full program. Confirm on SmartPAD / Live IO while a job runs, and against the installed control release vs Caracol Ver3.9.1 notes.

**Calibration implication:** V1 optimizes what MassiveSLICER actually emits (wipe / z-hop / resume wait / travel). Full OUT[7]/OUT[9] travel inject is a separate exporter feature if hardware still needs it.

---

## Workflow

1. Open `Start_and_Stop_Calibration_Effort_V1.mass` (or regenerate — see below).  
2. Select material + cell temps as usual.  
3. Confirm Additive: wipe, z-hop, resume wait, speeds.  
4. Re-slice if you changed motion params.  
5. Export KRL → dry-run → short print.  
6. Photograph seams; log settings in a notebook row.  
7. Tweak one knob; repeat.

### Regenerate workspace (from repo)

```csharp
// e.g. unit test / small tool calling:
StartStopCalibrationWorkspace.Create(new() {
    SavePath = StartStopCalibrationWorkspace.ProjectWorkspacePath(),
    Cell = cell,
    CellPath = "LFAM1/lfam1.json", // or LFAM2/LFAM3
    HomeAngles = cell.Robot.HomePosition,
});
```

Or open MassiveSLICER, place two primitives, and match the settings table above if you prefer a manual scene.

---

## Success criteria

- Clean stop: no long strings, no large blob at wall end  
- Clean start: bead reattaches within ~1–2 nozzle widths, no void  
- Stable across ≥10 layers (not just first/last)  
- Document winning numbers for production multi-island jobs  

---

## Related assets

| Path | Role |
|------|------|
| `.../2025_0523 - Start and Stop/Handover Start and Stop - V2.pdf` | Caracol training |
| `.../travel_ss-post.txt` | Example KRL with full OUT inject |
| `.../CARACOL_HF_HV_IS_TR 2.edsp` | Eidos post with URM/screw folds |
| `KrlExporter.DefaultHeaderTemplate` | MassiveSLICER job-level OUT[7]/OUT[9] |
| `MovementPostProcessor` | Wipe + z-hop insertion |
| `MaterialCalibrationWorkspace` | Sister pattern (purge cal) |
