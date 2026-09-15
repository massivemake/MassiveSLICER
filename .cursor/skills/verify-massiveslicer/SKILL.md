---
name: verify-massiveslicer
description: Drive MassiveSLICER (Avalonia/.NET desktop CAM) via its localhost LocalControlBridge to prove UI and workspace behavior. Use when verifying MassiveSLICER changes, slicing/import/workspace flows, or any agent task that must close the loop against the real app—not unit tests alone.
---

# Verify MassiveSLICER

MassiveSLICER is an Avalonia/.NET 8 desktop CAM app. Agents drive the **running GUI** through `LocalControlBridge` (HTTP on loopback, preferred port 8723). Do not use live robot motion commands (`move-*`, `sync`, `move-joints`, `calibrate`, `move-lin`, `move-home`) unless the user explicitly asked for a cell test and the cell is safe.

Helper: `powershell -NoProfile -File .cursor/skills/verify-massiveslicer/control-massiveslicer.ps1 <action> ...`

## Launch

From the repo root on Windows (SB101 / shop PC):

1. Prefer an already-built Release binary:
   `Start-Process .\src\MassiveSlicer.App\bin\Release\net8.0-windows\MassiveSlicer.App.exe`
2. Or rebuild and launch: `.\build-and-run.ps1`
3. Or: `dotnet run --project src/MassiveSlicer.App`

Ready when `%LOCALAPPDATA%\MassiveSlicer\bridge.port` exists and doctor succeeds:

```powershell
powershell -NoProfile -File .cursor/skills/verify-massiveslicer/control-massiveslicer.ps1 doctor
```

Record the PID of the process you started. Do not enable `MASSIVESLICER_BRIDGE_LAN` for verification.

## Doctor

```powershell
powershell -NoProfile -File .cursor/skills/verify-massiveslicer/control-massiveslicer.ps1 doctor
```

Must report `ok: true`, a port (usually 8723–8728), `ping.ok`, and at least one `MassiveSlicer.App` PID. If ping fails while a process exists, wait a few seconds for the window to finish loading, then retry. Never drive an instance you did not start for this run when another unverified session owns the bridge.

## Drive

All interaction goes through the helper (wraps GET/POST on `http://127.0.0.1:<port>`):

| Action | Helper | Bridge |
|--------|--------|--------|
| Health | `doctor` / `ping` | `GET /ping` |
| Robot/status | `status` | `GET /status` |
| Console log | `console -N 40` | `GET /console?n=N` |
| Screenshot | `screenshot -Path artifacts/.../x.png` | `GET /screenshot?format=png` |
| Console cmd | `command help` / `command new` / … | `POST /command` `{"command":"..."}` |
| Open .mass | `command open "<path>"` (any path); helper `open` = MassiveFILES only | `POST /command` or `POST /open` |

Prefer stable console command names from `help` over clicking the Avalonia tree. Read `features/README.md` and the matching feature file before proving a feature.

Safe starter proof: `command new` then `screenshot` (see `features/screenshot-bridge.md` and `features/workspace.md`).

## Evidence

- Write proofs under `.cursor/skills/verify-massiveslicer/artifacts/<feature-id>/`.
- Capture both the **action** (command JSON / console lines) and the **result** (screenshot PNG and/or second console read).
- Screenshot proof must show the MassiveSLICER window chrome (not a blank/black-only frame).
- Side effects for workspace: after `new`/`save`, confirm via console output or a follow-up `command` that observes state—not only HTTP `ok`.
- Keep proofs after cleanup. `artifacts/` is gitignored except its README.

## Cleanup

- Kill **only** the MassiveSlicer.App PID you started for this verification run (from doctor output / launch notes). Do not `Stop-Process -Name MassiveSlicer.App` if other instances may be the user's session.
- Leave `artifacts/` untouched.
- Do not delete `%LOCALAPPDATA%\MassiveSlicer\screenshots\` wholesale; those are app-owned copies.

## Helpers

```powershell
# from repo root
$ctrl = '.cursor/skills/verify-massiveslicer/control-massiveslicer.ps1'
powershell -NoProfile -File $ctrl doctor
powershell -NoProfile -File $ctrl command help
powershell -NoProfile -File $ctrl command new
powershell -NoProfile -File $ctrl screenshot -Path .cursor/skills/verify-massiveslicer/artifacts/screenshot-bridge/window.png
```

Feature map: `features/README.md` (includes `ik-fk-scrub` for sim IK/FK and `lfam3-dual-bed` for shop heated vs rotary beds).

Safe starter proof for kinematics: see `features/ik-fk-scrub.md` — never `sync` / `move-*` in default verify.

## Launch without Open File Security Warning

Do **not** Start-Process the Release exe directly from `Z:\` (network zone warning). Prefer:

1. Sync: `robocopy Z:\Research\LFAM\MassiveSLICER\src\MassiveSlicer.App\bin\Release\net8.0-windows C:\Users\MassiveMAKE\Apps\MassiveSlicer.App /MIR`
2. Start: `Start-Process C:\Users\MassiveMAKE\Apps\MassiveSlicer.App\MassiveSlicer.App.exe -WorkingDirectory Z:\Research\LFAM\MassiveSLICER`

HKCU ZoneMap already maps `192.168.0.191` to Local Intranet for this user; local copy is still the reliable agent path.