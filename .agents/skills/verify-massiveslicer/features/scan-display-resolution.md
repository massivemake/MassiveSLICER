# Scan view resolution

Preferences → Performance → Lower scan view resolution. It sits under Optimized scan merge. Checked is the default. It is a machine preference in prefs.json. Opening a workspace does not copy it, and it does not reslice.

Checked draws a solid lower-resolution surface. Each drawn vertex is one of the measured vertices, and each cell of the scan is filled so the view stays a surface instead of scattered specks. Alignment, milling, paint, and export keep the full scan. Unchecked draws every measured vertex. Neither choice moves a scan.

A scan already under about 180,000 triangles or points is drawn whole. A larger scan keeps the measured corners of its bounds, so the thinned view spans the same size as the full scan.

## Sub-features

- Checkbox `Lower scan view resolution` under the SCANS heading on the Performance page
- Checked thins the drawing only
- Unchecked draws the full scan
- `viewset ReducedScanDisplay` reads it, and a value writes prefs.json

## How to get to it (user POV)

Open Preferences. Choose Performance. The Scans section is under anti-aliasing. This checkbox is the second one, under Optimized scan merge. It is on by default.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "viewset ReducedScanDisplay"
```

Pass: a fresh preference reads `True`. `viewset ReducedScanDisplay false` selects the full drawing and writes prefs.json. Read it back as `False`. Set it back to `True` when the check is done. Do not merge the open Rock scans to prove it. Do not save the workspace.

## Gotchas

- The open app keeps the binary it launched with until a restart. This checkbox is not in that process.
- The choice is shared by every window on this machine. It is not applied from the .mass file.
- Mill, export, and the scan aligner read the full positions, not the thinned drawing. Scan positions are not written.
- Mill area paint keeps the thinned drawing. The green wash uses those thinned corners. Switching between mill and scan does not put the full mesh back on the card. Milling still reads the full positions.
