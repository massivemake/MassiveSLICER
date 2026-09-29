# T12 overlay

LFAM 3 mill TCP triad + lime bit. Position = mill bit-tip. Axes = pad TOOL_DATA ABC. FLANGE stays at A6.

## Sub-features

- VIEWPORT → TCP axes on: triad visible
- Idle overlay (no mill path) still shows mill-nose triad
- RGB = XYZ (X red, Y green, Z blue)
- Robot connected vs not: overlay still reads; do not jog

## How to get to it (user POV)

Load LFAM 3, MILL, tool 11 or 12 mounted. Enable TCP axes. Look at flange vs mill housing vs triad.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py status
python3 .agents/skills/verify-massiveslicer/scripts/drive.py screenshot --out "$HOME/Library/Application Support/MassiveSlicer/verify-evidence/t12-overlay.png"
```

Pass: `/status` `tool` is 11 or 12 when that mill tool is mounted; screenshot shows triad on the mill nose, not under the bed, not parked at Mill Start while claiming path follow. Quote overlay numbers; do **not** rewrite Mill Start joints (`0/−90/90/0/45/105`) or `lfam3.json` TOOL_DATA to “fit” the picture.

## Gotchas

- Pad T12 XYZ (−78/325/637) mill-down from the flange lands **under the bed**. Overlay position is bit-tip, not that XYZ.
- `$POS_ACT` is live measurement. Path overlay ≠ new home.
- Green cutter mesh is not axis Y.
