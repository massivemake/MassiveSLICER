---
name: verify-massiveslicer
description: "Prove live MassiveSLICER via the :8723 bridge."
version: 0.1.0
author: Thom Boessel, MassiveAI
license: MIT
platforms: [macos, windows]
---

# Verify MassiveSLICER

Scripted proof of the **running app**, not `dotnet test`. The suite has 15 known failures (`docs/KNOWN-TEST-FAILURES.md`); green-enough tests are not user-path proof.

Drive LocalControlBridge (`127.0.0.1:8723–8728`). Lab MCP is ERP — not this.

## When to Use

- After a Slicer UI / mill / KRL / overlay / home change
- User asks if it actually works, to screenshot, or to dogfood the desktop app
- Before claiming a mill/IK/export fix

Don't use for: Lab ERP, MassiveDRIVE RSI, or installing pstack.

## Prerequisites

- Checkout is `/Volumes/MassiveFILES/Research/LFAM/MassiveSLICER` (shop `Z:\Research\LFAM\MassiveSLICER`). Never `~/MassiveSLICER`.
- Bridge starts with the app. Port file: macOS `~/Library/Application Support/MassiveSlicer/bridge.port`, shop `%LOCALAPPDATA%\MassiveSlicer\bridge.port`.

## Launch

Do **not** start a second instance if doctor already returns `worth_driving`.

**Mac (MassiveFILES is noexec):**

```bash
cd /Volumes/MassiveFILES/Research/LFAM/MassiveSLICER
export DOTNET_ROLL_FORWARD=Major
bash run.sh
```

Ready: console `[bridge] control API on http://…` **or** doctor exit 0. GL `[gl] init … Metal` is enough.

**Shop:** Release `--no-incremental`, then launch the rebuilt exe. Console must show `[bridge]`. LAN only if `bridge.lan` = `1` (POST `/command` can move the robot).

Teardown: quit the instance **you** started. Never `killall MassiveSlicer` / `killall dotnet` — that can kill the operator's session. Proof files stay; do not delete them.

## Doctor

Read-only. Completion: JSON `ok: true` and `/ping` `app=MassiveSlicer`.

```bash
python3 .agents/skills/verify-massiveslicer/scripts/doctor.py
```

If exit 2: launch (above), then doctor again. If `connected: true`, treat every `/command` as motion-capable.

## Drive

Helpers (NAS is noexec — always `python3`, never `./`):

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py ping
python3 .agents/skills/verify-massiveslicer/scripts/drive.py status
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "mill status"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py screenshot
```

`drive.py cmd` blocks `home` / `sync` / `move*` unless `--allow-motion` **and** the user asked to move the robot.

Raw curl:

```bash
curl -sS -m 2 http://127.0.0.1:8723/ping
curl -sS -m 2 http://127.0.0.1:8723/status
curl -sS -m 5 -H 'Content-Type: application/json' \
  -d '{"command":"mill status"}' http://127.0.0.1:8723/command
```

Prefer console names (`mill`, `krlpost`, `outliner-tree`, `select`) over clicking pixels. Screenshot is supporting evidence, not the only proof.

## Evidence

Keep proof **outside** git:

`~/Library/Application Support/MassiveSlicer/verify-evidence/` (shop: `%LOCALAPPDATA%\MassiveSlicer\verify-evidence\`)

For each check: command + output JSON, `/status` snapshot, screenshot path if UI. Side effects (`.src` on disk, console `[krlpost]` lines) beat “the panel looks right.”

Mocks only at a real production boundary (no robot). Never stub the viewport.

## Cleanup

If you launched the app, quit that process. Leave `verify-evidence/` and bridge screenshots. Do not wipe `%LOCALAPPDATA%\MassiveSlicer`.

## Feature map

Index: `features/README.md`. Drive the mapped entry points, not one convenient command.

## Pitfalls

- `dotnet test` ≠ this skill. Compare new failures to `docs/KNOWN-TEST-FAILURES.md`; do not stash to get a baseline.
- Generating mill with no user part mills **HeatedBed**. Skip `PickTier.Environment`.
- `home` with no args **goes** to saved home (Drive PTP). Doctor/drive reads only.
- Screenshot path is on the **Slicer PC**. This Mac can read it only if it is this Mac.
- Do not register `scripts/mcp/massiveslicer_mcp.py` in Hermes until `/ping` works.
- Shop visual claims need a **Release --no-incremental** rebuild, then this skill on **that** binary.

## Verification

1. `doctor.py` exit 0.
2. Drive **one** mapped feature end-to-end; evidence still on disk after cleanup.
3. Motion commands were not sent unless the user asked.
