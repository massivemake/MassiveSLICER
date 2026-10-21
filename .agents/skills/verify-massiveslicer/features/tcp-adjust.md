# TCP Adjust

Spherical attractors that lean the extruder toward a cylinder you aim. On the cylinder the tool matches that angle. Farther away the lean fades, and outside the sphere the path is unchanged.

## Sub-features

- Section TCP ADJUST sits under STOCK FROM MAPS on the SLICE card
- Add places a sphere and an amber cylinder. Move places it. Rotate aims the cylinder. The influence slider is the sphere radius
- Lean X and Lean Y aim the cylinder from the panel, ±80°
- The tilt is written onto each move's normal after orientation smoothing, so export follows it
- Mill moves and layer stitches are left alone
- The handles save on the workspace, not in prefs

## How to get to it (user POV)

Right panel, step 2 SLICE, open TCP ADJUST, Add attractor. Move and Rotate the cylinder. Slice.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj add"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj lean 25 -15"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj radius 300"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj remove"
```

Pass: `tcpadj` prints `[tcpadj] none` on an empty scene, and `tcpadj add` prints a line with a radius and a position. `outliner-tree` shows a row flagged `TcpAttractor`.

`tcpadj add` puts a handle in the scene and schedules a slice if realtime slicing is on. On an empty scene that slice has nothing to cut. Do not add one on a job the operator has not saved.

## Gotchas

- The open app keeps the binary it launched with until a restart. The section is not in that process.
- This changes the move normal (the nozzle direction). It does not change TOOL_DATA length.
- Two attractors that weigh a point the same: the first one wins.
- Roll around the cylinder is not a slider. Aim it with Lean X, Lean Y, or the rotate gizmo.
- A saved `.mass` from before this field loads with no attractors.
