# Drill holes

Find round holes and sharp edges on the selected model, including holes in a solid part, then pick the ones to drill and set each hole's diameter and depth.

## Sub-features

- TOOLPATHING → HOLES, a section of its own, separate from PASSES. It shows only while the operation is Drilling.
- Find holes and edges. A solid part's hole is the sharp round rim, not an open gap in the mesh. The two mouths of one through-hole are one hole, entered from the top.
- The list under HOLES works like the attractor list. Click a row to select that hole and show its diameter and depth. REMOVE takes a hole out of the drill, or deletes a point placed on an edge or a face.
- Ctrl-click a hole or an edge. The drill point goes at the center of that rim, and the rim highlights. Ctrl-click a face to drop a point there. A face point shows X Y Z so the spot can be typed. Ctrl-Alt-click removes a placed point.
- Hole diameter and Hole depth edit the selected hole only. An unedited depth is the mesh thickness at that hole plus Breakthrough.
- A hole wider than the bit bores a circle at that diameter. A hole the bit already fills is a plunge.
- Generate with no points selected does not drill the middle of the mesh.

## How to get to it (user POV)

LFAM 3 → MILL → Drilling. Select the part, not the bed. Open HOLES. Find holes and edges. Click a row, or click the hole on the part. Edit diameter and depth for that row. Generate Toolpath.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "mill op Drilling"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "mill drill"
```

Pass: `mill drill` reports holes, edges, and the selected point. `mill drill size` and `mill drill depth` change only the selected hole.

Do not Generate against the heated bed. Do not move the robot.
