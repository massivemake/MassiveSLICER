# Export polyline (Blender OBJ)

File → Export Polyline (Blender)… writes the selected toolpath as a Wavefront OBJ of line segments. Outliner right-click on a toolpath does the same. An imported KRL is the whole program, travels included.

## How to get to it (user POV)

Import KRL, select that path in the outliner, File → Export Polyline (Blender)…. Or right-click the path → Export as Polyline (Blender)….

Blender: File → Import → Wavefront (.obj). Numbers are millimeters, Z up. If the Blender scene is meters, set Scale to 0.001. Object → Convert → Curve if you want a curve object instead of edges.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "export-polyline /tmp/slicer-polyline.obj"
```

Pass: console `[polyline] wrote /tmp/slicer-polyline.obj` and the file contains `# units: millimeters` and `l ` lines, no `f ` faces. Do not `home` / `sync` / `move*`.

No toolpath: `[polyline] export failed` and the slice status says to import or select one.

## Gotchas

- Drawn pose, not raw KRL BASE numbers. A path you moved in the viewport exports where it sits.
- A gap in the path starts a new chain. No invented chord across a teleport.
- Does not change KRL export or Drive Send.
