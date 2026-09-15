# Viewport camera

Camera console commands orbit, zoom, pan, and frame the scene without touching the robot.

## Sub-features

- `cam-orbit` / `cam-zoom` / `cam-pan` set absolute azimuth/elevation, radius, and look-at.
- `cam-frame-all` / `cam-focus` / `cam-preset` frame or snap the view.
- `cam-debug` prints camera state for console proof.

## How to get to it (user POV)

- Console camera commands listed in `help` (`cam-orbit`, `cam-zoom`, `cam-pan`, `cam-frame-all`, `cam-focus`, `cam-preset`, `cam-debug`; siblings `frame` / `frame-all`).
- Mouse orbit/zoom in the 3D viewport.
- Toolbar frame-all uses the same path as `cam-frame-all`.

## Driving it with control-massiveslicer

Preconditions:

- `doctor` is ok.
- Scene has something to look at (default cell/bed is enough).

- **Inspect.** Run `... command cam-debug`. Console shows Azimuth/Elevation/radius (or equivalent `[cam]` lines).
- **Frame all.** Run `... command cam-frame-all`. Response `ok: true`.
- **Orbit.** Run `... command cam-orbit 135 25` (absolute azimuth elevation). Re-run `cam-debug` and confirm values changed.
- **Proof.** Screenshots before/after under `artifacts/viewport-camera/`. Camera change must be visible, or console must echo the applied camera state.

## Gotchas

- `cam-orbit` / `cam-zoom` / `cam-pan` are absolute sets, not relative deltas.
- **`viewport-home` is not a camera command.** It resets viewport robot joints to the additive home preset. Do not use it for camera proof (see `ik-fk-scrub.md`).
- GL viewport may look black in OS screenshots; prefer bridge `screenshot`, which composites the viewport.

Visibility toggles (Grid / Bed grid / Axes) live in [viewport-overlays.md](./viewport-overlays.md) — Arctic shader hides Grid and Bed grid even when checked.
