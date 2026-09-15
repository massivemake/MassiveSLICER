# Screenshot bridge

`GET /screenshot` captures the full Avalonia window (with viewport composite) for proof artifacts.

## Sub-features

- `shot-json` returns metadata and a path under LocalAppData screenshots.
- `shot-png` returns raw `image/png` bytes for agent-controlled paths.

## How to get to it (user POV)

- Console: `screenshot` command inside the app.
- External tooling: LocalControlBridge HTTP endpoints (this skill’s primary path).

## Driving it with control-massiveslicer

Preconditions:

- `doctor` is ok.
- Main window is visible and sized (not minimized to zero).

- **Capture PNG.** Run `... screenshot -Path .cursor/skills/verify-massiveslicer/artifacts/screenshot-bridge/window.png`.
- **Proof.** File exists, size hundreds of KB for a normal window, and the image shows MassiveSLICER chrome. Keep helper JSON stdout beside the PNG.

## Gotchas

- Wait until doctor/ping succeed after launch before capturing.
- Do not treat a tiny/blank PNG as success — rerun after the window finishes loading.
