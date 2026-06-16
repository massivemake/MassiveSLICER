# Progress Log — Scanning, Rotary Bed & Camera

Running log of the scanning / rotary-bed calibration work. Newest first.
Robot controller setup for these features: see [KUKA_SETUP.md](KUKA_SETUP.md).

---

## 2026-06-16

### Remote program start — corrected to direct C3 program-run
- Discovered the bridge is the **ulsu-tech C3 Bridge Interface Server** (C3easy talks to it),
  which supports **program control** (protocol message type 10), not just variable RW.
- App now **selects + runs `/R1/Program/BED_SCAN_CAL` directly** via C3 Run (cmd 6, force) —
  no CELL dispatcher, no pendant Start, just AUTO mode. Matches the C3easy workflow.
- Rewrote `C3BridgeClient` with **length-based response framing** (uses the Message-Length
  field) + `RunProgramAsync` / `SelectProgramAsync` / `ProgramControlAsync`.
- Reverted the earlier `cell.src`/`cell.dat` dispatcher edits (restored from backup).
- ⚠️ Verify the program-name path on first run (app logs the C3 error code); see KUKA_SETUP.

## 2026-06-15

### Save View (camera)
- **Bottom-left "Save View" button** in the viewport overlay. Saves the current orbit-camera
  pose (azimuth / elevation / radius / target) into the **cell JSON** (`bed`-level `view`).
- Because it's stored in the cell file (on the shared network repo, not per-user AppData),
  **every user opens to the latest saved angle**. Applied on cell load, overriding the
  default bed framing.
- Logs to the console on save. Files: `CameraView` model + `CellConfig.View`,
  `CellLoader.SaveCameraView`, `ViewportViewModel.SaveViewCommand` + `GetCameraState`,
  `MainWindowViewModel.SaveCurrentView`, applied in `ViewportView.ApplyCellSwap`.

### Automated rotary-bed calibration (remote E1 sweep)
- **AUTO-CALIBRATE (E1 SWEEP)** button. App deploys `BED_SCAN_CAL.src`, triggers it remotely
  via the controller's existing `CELL()` dispatcher (`bRunBedScanCal` flag — no pendant Start),
  and captures 10 board scans across a −180→+180 E1 sweep, then fits the result.
- Handshake: `$FLAG[1]` (robot "scan now" → app clears) / `$FLAG[2]` (done). `C3BridgeClient.WriteAsync`
  added for KUKAVARPROXY writes. Controller `cell.src`/`cell.dat` extended (backups in `assets/krl/controller_backup/`).
- ⚠️ Verify on real robot: `$FLAG` writability, E1 ±180 limits, `PTP {E1}` partial move (see KUKA_SETUP).

### Rotary-bed rotation calibration
- Reuses the circle-fit samples to also fit **marker angle vs E1** → rotation **direction (sign)**,
  measured °/E1 (≈±1 sanity), and angular residual. Replaces the hard-coded rotation sign.
- Stored as `bed.rotationSign`; "Reverse E1 rotation" checkbox for manual override.

### Rotary-bed centre/Z calibration + UI
- Circle-fit (`RotaryBedCalibration`) of board centroids across E1 → bed **centre X/Y**, **Z height**,
  radius, RMS residual, Z-spread (tilt flag). Manual sample collection + Compute + Apply.
- **ROTARY BED** panel (left): editable Centre X/Y/Z, Diameter, E1, Reverse-rotation. Live + autosave to cell.
- **Circular polar grid** for LFAM3 (6 ft = 1828.8 mm): outer circle + concentric rings + radial spokes,
  centred on the bed axis. Driven by `bed.diameter`. Rectangular beds unchanged.
- E1 now rotates the **bed mesh + grid** about the calibrated centre, by `rotationSign × E1`.

### Hand-eye (scanner) calibration — FIXED
- Root cause of mis-registered scans: calibration was fed the **analytic FK flange** while
  registration used the **rendered glTF flange** (different frames). Now both use the rendered flange.
- Verified on-robot: scans register correctly.

---

## Open items
- `$FLAG` handshake writability unverified (fallback: `$config.dat` globals).
- Writing measured centre to the KRL "Base Rotary" frame (`$config.dat BASE_DATA[2]`) — deferred.
- "pointinfo collection is null" error seen once on first auto-cal attempt — not yet root-caused; revisit if it recurs.
