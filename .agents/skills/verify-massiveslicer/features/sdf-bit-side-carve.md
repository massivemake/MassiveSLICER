# SDF bit side carve

The SDF view can subtract the milling bit's stick-out cylinder from the rounded beads. Where the side of the bit cuts through an earlier bead, that thickness is removed. The cutting face is left alone, so a normal stepover on the same plane stays. This is a viewer pass. It does not change the mill path, the KRL, or the robot.

The checkbox is **Carve bit sides** in the SDF caret menu (ADVANCED VIEWING). It is off until turned on. It is not saved in the scene.

## Sub-features

- The cylinder matches the green spindle bit: tool diameter, stick-out length, and the flip flag
- A ball-end bit sweeps a sphere of that radius on the tip. The toolpath point is the bottom of the ball. The shader cuts that sphere exactly, including the overlap between passes, so a small ball stays round instead of following the voxel grid. The floor beside the tip, outside that sphere, stays. The shank above the equator still uses the voxel grid
- A flat end keeps the cutting face. Same-layer beads that sit on that face are kept
- A bead up the stick, inside the tool radius, is eaten
- Turning it off draws the original beads again

## How to get to it (user POV)

Open a mill toolpath. Choose the SDF pill. Open the caret next to it. Check **Carve bit sides**. The first build can take a few seconds. The view updates when the grid is ready.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "viewmode SDF"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "viewset SdfCarveBitSides"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "viewset SdfCarveBitSides true"
```

Pass: the get prints `False` before the set. After `true`, a later screenshot of the SDF view differs from the carve-off shot, and the app log contains `[sdf] bit side carve:`. `viewmode Toolpath` after SDF still shows the path. Do not save the scene. Do not move the robot.

## Gotchas

- A flat end uses the cylinder side above the cutting face. A facing stepover is kept.
- A ball end (the bit's Ball end type) sweeps a sphere on that tip. The programmed point is the bottom of the sphere. Material beside the tip, outside the sphere, stays. The scoop is the analytic sphere, so it does not pick up the voxel stair-step.
- Cell size grows on a very large part so the shank grid stays under about 28 million cells. A flat end's gouge edge is then a few millimetres soft. A ball end's nose is the real sphere.
- Samples along a straight stick are kept about every 2–6 mm. A lean change is always kept.
- Additive beads are not carved unless a mill move's stick actually passes through them.
- A mill path that sits directly under the rotary group (not under the mesh row) stays visible in SDF, Preview, and the line views. Body still hides it.
