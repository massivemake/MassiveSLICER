# Robot IK/FK simulation (scrub and reachability)

Viewport IK/FK drives the on-screen robot for scrub and reachability without moving a live cell. Unit tests cover solvers; this feature proves the GUI/scrub/marker/bridge loop.

## Sub-features

- `viewport-home-fk` — `viewport-home` sets sim joints; FK tip/joints via `status` / `robotset` / screenshot.
- `scrub-ik` — with an armed toolpath, `scrub` drives the viewport robot via Scrub IK.
- `reachability-markers` — after slice/validation, scrubber shows unreachable (red) and/or singularity (purple) markers; stats/warnings may appear.

## How to get to it (user POV)

- Robot panel joint sliders / home preset (sim FK).
- Toolpath timeline scrub after slice (IK pose + markers).
- Console: `viewport-home`, `scrub`, `robotset`; bridge `GET /status`.

## Driving it with control-massiveslicer

Preconditions:

- `doctor` is ok.
- `status` shows `connected: false` (do not `sync`).
- A toolpath exists (run `import-and-slice` first, or open a `.mass` that already has one).

- **Sim home.** Run `... command viewport-home`. Expect `[viewport-home] A1=…` lines.
- **Read FK.** Run `... status` and/or `... command robotset A1` (read). Screenshot under `artifacts/ik-fk-scrub/`.
- **Scrub.** Run `... command scrub show` then `... command scrub pct 50`. Re-read status/robotset; joints/TCP/robot mesh should change.
- **Markers.** Screenshot scrubber/timeline for red/purple markers, or note console validation text.
- **Proof.** Keep command JSON + screenshots under `artifacts/ik-fk-scrub/`.

Hard bans in this recipe: `sync`, `move-*`, `move-joints`, `move-pose`, `move-lin`, `move-home`, `home`, `calibrate`. Scrub IK may call Desync — keep the cell disconnected for the whole proof.

## Gotchas

- `pos` / `joints` require Sync — exclude from the default recipe.
- `cal-check` without Sync only reports the scene side.
- `viewport-home` is joints, not camera framing (see `viewport-camera.md`).
- Solver unit tests complement this feature; they do not replace it.
- Live proof 2026-09-15 (SB101, connected:false): `viewport-home` ? A1�-0.22; after `scrub pct 50` A1�-4.53 and SceneTcpZ moved 1106?283; `scrub pct 0/100` further changed SceneTcpZ (132.9 / 433.1). Bridge `status.pose` also updates while disconnected.
- Prefer `robotset` over `status` immediately after `viewport-home` if pose looks stale.
- `control-massiveslicer.ps1 screenshot <path>` still lands under `artifacts/latest/` and `%LOCALAPPDATA%\MassiveSlicer\screenshots\` � copy into `artifacts/ik-fk-scrub/` yourself.