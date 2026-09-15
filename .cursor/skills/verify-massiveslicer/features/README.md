# MassiveSLICER verification map

Maintained recipes for driving MassiveSLICER through `control-massiveslicer.ps1` and LocalControlBridge. Read this index, then the matching feature file.

## Baseline preconditions

- MassiveSLICER GUI is running on Windows with LocalControlBridge on loopback (preferred port 8723).
- `control-massiveslicer.ps1 doctor` reports `ok: true`.
- You started (or were handed) the PID for this verify run; do not drive a random shared session for destructive workspace tests.
- Do not enable LAN bridge mode for verification.
- Never run live robot motion commands unless the user ordered a cell test (`sync` / `move-*` banned in default recipes).

## Driving conventions

- Start from a known workspace state (`command new` unless the feature says otherwise).
- Prefer console command names from `command help` over UI coordinates.
- Treat every helper invocation as literal.
- Record the feature ID on every artifact path under `artifacts/<feature-id>/`.
- HTTP `ok` after `POST /command` is not enough when the command opens a picker or starts async work — confirm console text.

## Proof and skip reporting

- Capture the command response JSON and a screenshot (or console dump) of the resulting state.
- Mutation proof needs a second observation after the action.
- Report unreachable paths with the unmet precondition; do not claim a different entry point verified them.

## Features

- [Workspace new/open/save](./workspace.md)
- [Import and slice](./import-and-slice.md)
- [Viewport camera](./viewport-camera.md)
- [Screenshot bridge](./screenshot-bridge.md)
- [Robot IK/FK simulation (scrub and reachability)](./ik-fk-scrub.md)
