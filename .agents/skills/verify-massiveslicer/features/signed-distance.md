# Signed Distance

Slice method from the Atomizer paper (Chermain et al., Computer Graphics Forum 2025). Bead centers sit on shells of constant distance inside the solid. The first shell is half a bead in from the surface. Each further shell steps in by one bead width. On a shell, contours are spaced one layer height apart, climbing from the bottom the way Geodesic does. The nozzle follows the outward signed-distance gradient, mirrored so it never aims downward.

Surface follow, max tilt, and first-layer zero tilt apply, the same sliders Geodesic and Curved use.

## Sub-features

- SLICING METHOD combo gains `Signed Distance`
- Layer height spaces the contours. Bead width spaces the shells
- Each extrude move carries that outward normal, so export aims the tool with the surface
- A part longer than 64 cells on one side coarsens the distance grid and says so in the slice warnings
- Filling stops at 48 shells. A thicker core is left unfilled and the warning says so

## How to get to it (user POV)

Right panel, step 2 SLICE, open SLICING METHOD, choose Signed Distance. Slice.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "sdf"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset MethodDisplayName"
```

Pass: `sdf` prints `listed=True` and the active method name. `addset MethodDisplayName` with no value prints the current method.

Do not pass a value to `addset MethodDisplayName`. A value selects the method and reslices the open job.

## Gotchas

- The open app keeps the binary it launched with until a restart.
- Wall count, infill, and planar surface mode do not apply. The shells are the fill.
- Contours start one layer height up from the bottom of each shell, so a flat bottom is not hatched.
- The grid is capped at 64 cells on the longest side. A metre-scale part at a 3 mm layer is coarser than the layer height.
- An open mesh can come back with no shells. The warning says the mesh may be open or thinner than one bead.
