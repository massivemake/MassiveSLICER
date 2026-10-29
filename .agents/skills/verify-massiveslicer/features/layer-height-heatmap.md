# Layer height heatmap

Colors the bead by layer thickness so adaptive layer height is visible. Blue is the thinnest cut in that toolpath, green is the middle, red is the thickest. A path whose layers are all one thickness is green. The checkbox is a view toggle. It is not stored in the workspace and it does not reslice.

## Sub-features

- Checkbox `Layer height heatmap` sits directly under `Bead overhang heatmap` in the data visualizer
- The legend at the bottom right says Thin, Mid, Thick
- Turning it on clears bead overhang and orientation preview, and the reverse
- Scores are built when the toolpath is uploaded. Toggling the checkbox only shows the map already on the GPU

## How to get to it (user POV)

Select a toolpath. Open the data visualizer (the scatter-plot toolbar button). Check Layer height heatmap, under Bead overhang heatmap. The legend sits at the bottom right.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "viewset ShowLayerHeightHeatmap"
```

Pass: a fresh view reads `False`. `viewset ShowLayerHeightHeatmap true` shows the legend and turns the other two bead color modes off. Set it back to `False` when the check is done. Do not leave it on against a job.

## Gotchas

- The open app keeps the binary it launched with until a restart.
- Slice-plane lines stay uncolored. The map is the bead surface.
- Export, Drive Send, mill planning, and scan poses do not read these colors.
- Thickness is the stored layer height times the move's height scale. When a layer has no stored height, the score uses the gap to the neighbouring layer Z.
