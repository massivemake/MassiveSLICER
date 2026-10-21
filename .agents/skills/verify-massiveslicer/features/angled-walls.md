# Angled walls

Constant-tilt Angled slicing in Normal mode can print concentric shells. 1 is the outer wall. Each + adds an inner wall one bead width further in.

## Sub-features

- + / − next to Slicing mode, only when Method is Angled and Slicing mode is Normal
- Reslice keeps the walls
- Surface, zig-zag, Planar, and Multi-Planar ignore the count
- `addset WallCount N` reads and writes it

## How to get to it (user POV)

Method = Angled. Slicing mode = Normal. Walls row, press +. Slice.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset WallCount"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset WallCount 3"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset Method Angled"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset SlicingMode Normal"
```

Pass: `addset WallCount` prints `3` after the set. A following slice of a solid has more than one closed loop per layer. Setting it back to `1` returns a single loop.

`addset` writes the property and the realtime slice watch reslices. Do not send it against a job the operator has not saved.

## Gotchas

- The control is hidden for Surface and for every method except Angled. The stored count remains.
- Formbound and X-bracing still replace the shell; extra walls are not added on those paths.
- The open app must be rebuilt before `addset WallCount` exists.
