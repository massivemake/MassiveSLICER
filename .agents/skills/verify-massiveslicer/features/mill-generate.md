# Mill generate

Planar / catalog mill toolpath from MILL → 2 OPERATION → 3 TOOLPATHING → Generate Toolpath.

## Sub-features

- Operation tiles: MultiAxisFinishing, Drilling, PlanarFacing, PlanarClearing, Cutout, Contouring, Swarf, Morph
- SELECT AREA: Whole / Face / Box / Lasso / Brush
- TOOL AXIS World ±X/Y/Z, paint, camera; tilt / azimuth
- Empty selection must **not** mill HeatedBed
- Generate with a user part or SVG selected

## How to get to it (user POV)

LFAM 3 → MILL. Pick a bit. 2 OPERATION → an op. Select the **part or SVG**, not the bed. Generate Toolpath.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "mill status"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "mill op PlanarFacing"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "outliner-tree"
```

Pass: `mill status` shows `operation=` the op you set, `target` is the user mesh/SVG (never `HeatedBed`), travel speed is mill travel (default 80 mm/s) not print 600. After Generate, outliner has a mill toolpath child on that part.

Do **not** Generate in an empty cell as a “smoke test.”

## Gotchas

- No selected user part + Generate = HeatedBed mill + Unreachable. Skip environment nodes.
- Print MOTION TravelSpeed is the wrong field on mill paths.
- Console `mill op Cutout` is Planar Cut (single-line SVG). Open strokes autoclose on import.
- Shop must Release-rebuild before claiming a mill generate fix.
