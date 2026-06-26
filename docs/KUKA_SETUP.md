# KUKA Controller Setup — Remote Bed Calibration & Scanning

What must be configured on the KRC4 (`192.168.0.153`) for the app's automated
rotary-bed calibration (and remote program start) to work.

Status legend: ✅ done by us · ⚠️ you must verify/do on the controller.

---

## 1. Network / C3Bridge  ✅ (already running)
- KUKAVARPROXY / C3Bridge server running on the controller, **TCP port 7000**.
- App reads `$AXIS_ACT` / `$POS_ACT` and writes global BOOL flags through it.
- Controller share reachable at `\\192.168.0.153\krc` (used to deploy `.src` files).

## 2. Remote program start — via the C3 Bridge (no dispatcher, no pendant Start)
Your bridge is the **ulsu-tech C3 Bridge Interface Server** (the one C3easy / "C3 Easy
Control" talks to). It supports **program control** (message type 10), so the app selects
and runs a program directly — exactly like picking an `.src` in C3easy and pressing Run.
No `CELL()` dispatcher and no pendant Start required.
- ✅ `…\R1\Program\BED_SCAN_CAL.src` deployed (app also re-deploys it on AUTO-CALIBRATE).
  Repo source of truth: `assets/krl/BED_SCAN_CAL.src`. KRL program path: `/R1/Program/BED_SCAN_CAL`.
- The app sends C3 **Run** (type 10, cmd 6, force=TRUE) with that program path.
- `cell.src` / `cell.dat` were **left untouched** (an earlier dispatcher experiment was
  reverted; originals + the experiment are in `assets/krl/controller_backup/`).

## 3. Operating mode & safety  ⚠️
- Robot in **AUT**, **drives ON**, no active faults. (The app issues the C3 Run command;
  KUKA still enforces mode/drives — but treat it as remote-commanded motion: first run,
  keep a hand on the e-stop.)
- `BED_SCAN_CAL` moves **only E1** (the rotary bed) — the arm A1–A6 stay put. Position
  the arm + scanner over the bed first and confirm the scanner clears the bed through a
  full rotation.
- Running `BED_SCAN_CAL` selects it as the active robot program (deselecting whatever was
  selected, e.g. CELL) — same as running any program from C3easy.

## 5. Things to verify on the real robot  ⚠️
- **Program-name path.** The app runs `/R1/Program/BED_SCAN_CAL` (constant `programName`
  in `MainWindowViewModel.RunAutoBedCalibration`). If C3 returns a non-zero error code on
  start, the path is the likely cause — try `/R1/BED_SCAN_CAL` or a bare `BED_SCAN_CAL`,
  or move the `.src` to match. The app logs the C3 error code to the console.
- **`$FLAG[1]` / `$FLAG[2]` writable via your C3Bridge.** The per-scan handshake uses
  them (`$FLAG[1]` = "at position, scan now" → app clears it; `$FLAG[2]` = "done").
  If the sweep starts but stalls at the first scan, your bridge is blocking `$FLAG`
  writes → move these two flags into `$config.dat` globals (they're then global to all
  programs and writable like the `bRun*` flags). Tell the dev and it's a 2-line change.
- **E1 software limits cover ±180°.** The sweep targets −180 → +180 in 40° steps. If
  E1's limits are tighter, reduce the range in `BED_SCAN_CAL.src` (`-180.0 + i*40.0`).
- E1 motion uses an **`E6AXIS` snapshot** (`apos = $AXIS_ACT`; `apos.E1 = …`; `PTP apos`),
  so the arm holds its current pose and only E1 turns. (Inline `PTP {E1 var}` is invalid
  KRL — "constant expected" — hence this form.)
- Sweep speed is **20 %** (`BAS(#PTP_PARAMS, 20)`) — adjust in the `.src` if needed.

## 6. Scanner (Zivid) — for the calibration scans  ⚠️
- Zivid camera reachable (eye-in-hand on the flange), Zivid SDK 2.16 installed on the PC.
- Hand-eye calibration already done (scanner TCP correct).
- Calibration board taped **off-centre** on the bed so it sweeps a real circle.

---

## Sequence the app runs (AUTO-CALIBRATE button)
1. Deploy `BED_SCAN_CAL.src` to the controller (SMB copy).
2. Pause live polling; reset `$FLAG[1]=$FLAG[2]=FALSE`.
3. **C3 Run** `/R1/Program/BED_SCAN_CAL` (program control, type 10 cmd 6, force) — selects + starts it.
4. Per stop: wait for `$FLAG[1]`, read E1, capture a Zivid board scan, set `$FLAG[1]=FALSE`.
5. After 10 stops (or `$FLAG[2]`), resume polling and fit centre / Z / rotation.
6. Review the result, then **Apply to Cell**.

## Not yet automated
- Writing the measured centre into the KRL **"Base Rotary"** frame (`$config.dat`
  `BASE_DATA[2]`) — currently the app persists the centre to the cell JSON only.
