# Scan merge performance

Preferences → Performance → Optimized scan merge. Checked is the default. It is a machine preference in prefs.json. Opening a workspace does not copy it, and it does not reslice.

Checked runs the scan merge off the interface thread, uploads position and normal only, and frees a hidden scan's graphics buffer. Unchecked is the previous method: the merge runs on the interface thread, the upload is 14 floats per vertex, and hiding a scan keeps the buffer. Both paths use the same vertex positions and normals. A buffer that is already on the graphics card stays until that scan is hidden and shown again.

## Sub-features

- Checkbox `Optimized scan merge` under the SCANS heading on the Performance page
- Checked is the optimized path
- Unchecked is the previous merge and the full buffer
- `viewset OptimizedScanMerge` reads it, and a value writes prefs.json
- The next checkbox, Lower scan view resolution, is `scan-display-resolution.md`

## How to get to it (user POV)

Open Preferences. Choose Performance. The Scans section is under anti-aliasing. The checkbox is on by default.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "viewset OptimizedScanMerge"
```

Pass: a fresh preference reads `True`. `viewset OptimizedScanMerge false` selects the previous method and writes prefs.json. Read it back as `False`. Set it back to `True` when the check is done. Do not merge the open Rock scans to prove it. Do not save the workspace.

## Gotchas

- The open app keeps the binary it launched with until a restart. This checkbox is not in that process.
- The choice is shared by every window on this machine. It is not applied from the .mass file.
- Mill, KRL, and the scan aligner do not read this flag. Scan positions are not written by either path.
- An already uploaded scan keeps its current buffer until it is hidden and shown again.
