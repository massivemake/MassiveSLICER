# Scan model alignment

Scan tab → Alignment. The scan is the milling position and does not move. Match model to scan moves the model so it sits inside the scan by Stock thickness. The print is larger than the model. A weak fit writes an error under the button and leaves both meshes where they are. Offset and Rotate then nudge the model from the matched pose.

The match does not reslice and does not regenerate a mill. A toolpath that already belongs to the model is carried with it, the same way a typed move does.

## Sub-features

- Section `ALIGNMENT` under Hand-eye calibration
- `Match model to scan`
- Stock thickness, tolerance, minimum match, max shift, max rotation
- Error text directly under the button when the fit is rejected
- Offset X Y Z (mm) and Rotate X Y Z (degrees) after a match
- `scan-align status` reads the tuning values and does not move anything
- `scan-align` runs the match

## How to get to it (user POV)

Open the Scan tab. Alignment is the third card. Select one scan (the merged scan, when the print was merged) and the model to move. One visible model is used when no model is selected. The scan stays selected as the position source.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "scan-align status"
```

Pass: the line reports stock, tolerance, match, shift, and turn. Do not run `scan-align` without `status` on a saved scene. That command moves the model when the fit is good.

## Gotchas

- The open app keeps the binary it launched with until a restart.
- A model stored as several meshes is sampled from every mesh. The first mesh alone is not the part.
- A merged scan is sampled more densely than the model. A few thousand samples on a multi-metre sheet read as stock error.
- A scan of one face matches that face. The rest of the model is ignored, and minimum match is the percent of the scan on the model.
- A curved face is seated as a whole, on the scan's surface normal, not on one flat patch of that face.
- A face that sits off to the side of the scan, so the match would slide, is still not applied. Nudge instead, or scan more of the part.
- Match locates the model on the scan first, from any starting distance and from a large pitch, roll, or yaw, then fine-tunes. One scanned face is enough. The operator does not pre-align the model or raise Max rotation.
- Max shift and max rotation bound the fine match only. The locate step may move farther than max shift and turn farther than max rotation.
- A short one-face offset still does not slide the part along the scan. A move of more than 400 mm uses the full locate vote, including across that face.
- Scan vertices and the scan node transform are not written. The measured mesh stays the milling position.
- Stock thickness is how much extra material is outside the model. It is not the mill finish allowance.
