# Bead overhang colors

The bead overhang heatmap has two ramps. Off is white to red. On is blue (supported) through green to red (unsupported). The choice is an app preference, not part of the .mass file.

## Sub-features

- Checkbox `Bead overhang heatmap` turns the colored beads on
- A blank switch after the `BEAD OVERHANG` legend title picks the ramp
- The legend swatches in the corner follow that switch
- Turning the ramp on recolours the beads already on screen. It does not reslice

## How to get to it (user POV)

Select a toolpath. Open the data visualizer (the scatter-plot toolbar button). Check Bead overhang heatmap. The legend sits at the bottom right. The switch after BEAD OVERHANG changes the ramp. Off is white to red. On is blue, green, red.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "viewset BeadOverhangSpectrum"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "viewset ShowBeadOverhang"
```

Pass: a fresh preference reads `False` for the ramp (white to red). `viewset BeadOverhangSpectrum true` switches the ramp and writes prefs.json. Read it back as `True`. Set it back to `False` when the check is done so the operator's ramp stays where they left it. Do not turn the heatmap on against a job just to look, unless that window is the one under test.

## Gotchas

- The open app keeps the binary it launched with until a restart. The checkbox is not in that process.
- The ramp is shared by every window on this machine. It is not stored in the workspace.
- Orientation preview is a different rainbow and stays on its own checkbox.
- Export, Drive Send, and the mill path do not read these colors.
