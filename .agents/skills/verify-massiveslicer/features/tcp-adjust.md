# TCP Adjust

Attractors and deflectors that lean a tool or the robot flange toward a bar you aim. The volume is spherical or cylindrical. Align is a unit vector in the selected TCP. 0 0 1 is the old aim: the cutter on a mill tool, the wrist axis on the robot flange, and the nozzle aim on a print tool. On Tool that direction points down the stick. On Robot flange it points along the stick. While an attractor is selected, an amber arrow on that TCP shows the direction. The stick is an arrow too, and the two point the same way once the lean has taken. On Tool the stick head points down the bar. On Robot flange it points along the bar. Detractor turns the stick head around. A deflector stays a plain bar. The section is Attractor/Detractor. Check Detractor and that same direction leans away from the bar. A new handle is named attractor1, attractor2, and so on. An older file still loads TCP 1, TCP 2. Farther away the lean fades, and outside the volume the path is unchanged. Overlapping attractors add. Opposite arrows cancel, and agreeing arrows stack up to one full lean. A deflector still uses the strongest push. A deflector does not lock an angle. Inside the volume it turns the chosen frame back toward the way out, and it leaves an aim that is already heading out. Robot flange is sampled 400 mm back from the cutter. On a cylinder the stick is only the cylinder axis. A cylindrical attractor or deflector has its own diameter and height. A sphere keeps one influence radius. Old files keep a cylinder whose height equals its diameter, and an Aim of X, Y, or Z. New handles spawn on the origin of the BASE in use.

## Sub-features

- Section ATTRACTOR/DETRACTOR sits under STOCK FROM MAPS on the SLICE card
- Add places a sphere and an amber arrow. Move places it. Rotate aims the arrow. The influence slider is the sphere radius
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
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj deflect"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj deflect shape cylindrical"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj deflect diameter 600"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj deflect height 1200"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj deflect frame flange"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj shape cylindrical"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj diameter 600"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj height 1400"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj aim 0 0.71 0.71"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj detract on"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpadj detract off"
```

Pass: `tcpadj` prints `[tcpadj] none` on an empty scene, and `tcpadj add` prints a line with a radius and a position. `outliner-tree` shows a row flagged `TcpAttractor`. `tcpadj deflect` prints `DEF` with `frame=Robot flange`, and `outliner-tree` shows `TcpDeflector`. The printed `at=` is the active base origin, not the robot origin. After `tcpadj deflect shape cylindrical`, `diameter`, and `height`, the line includes `dia=` and `h=` with those millimetres. A cylindrical attractor line includes `dia=` and `h=`. `tcpadj diameter` then `tcpadj height` on that attractor keep the two sizes apart. An attractor line includes `aim=(0.00,0.00,1.00)` for the old Z aim. `tcpadj aim 0 0.707 0.707` prints a normalized `aim=(0.00,0.71,0.71)`. `tcpadj axis Z` sets that back. `tcpadj deflect aim` is rejected. `tcpadj add` names the new handle `attractor1` when that name is free. `tcpadj detract on` appends `detract` to that line, and `tcpadj detract off` removes it. `tcpadj deflect detract` is rejected.

`tcpadj add` puts a handle in the scene and schedules a slice if realtime slicing is on. On an empty scene that slice has nothing to cut. Do not add one on a job the operator has not saved.

## Gotchas

- The open app keeps the binary it launched with until a restart. The section is not in that process.
- This changes the move normal (the nozzle direction). It does not change TOOL_DATA length.
- Two attractors that weigh a point the same: the first one wins.
- Roll around the cylinder is not a slider. Aim it with Lean X, Lean Y, or the rotate gizmo.
- A saved `.mass` from before this field loads with no attractors.
