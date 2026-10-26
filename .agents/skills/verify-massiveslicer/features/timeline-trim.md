# Timeline section export

A playback-timeline section exports only that span of the active toolpath. The points stay where they are in the full program. Primary use is printing one stretch to test it.

## Sub-features

- Two flags on the preview timeline, next to the keyframe buttons. The outline flag sets the start at the playhead. The solid flag sets the end. A blue span under the keyframe lane shows the range. Drag either end. The playhead stays where it is.
- The caption reads `SECTION 100–200`. The start move is included. The end move is where the last line lands. A section from 100 to 200 keeps moves 100 through 199, so the approach goes to the world point at scrub 100 and the last LIN lands on scrub 200.
- With both flags set, the toolbar Save button and Send (Drive or robot) write that span. The save dialog suggests a `.src` whose name ends in `_m100-200`. Send to Drive uses the same suffix on the job name. Coordinates, normals, speeds, keyframe toolhead angles, and the rail position already planned for the full job are copied. The path is not moved to the origin and the layer is not restarted at 0, so a mid-print section does not take the first-layer speed.
- With the flags clear, Save and Send write the whole path.
- The marks are session-only, one range on the active scrub toolpath. They clear when a different toolpath is armed. They are not saved in the `.mass`.

## How to get to it (user POV)

Preview or SDF, with a toolpath on the playback timeline. Scrub to the first move of the stretch and press the outline flag. Scrub to the last move and press the solid flag. Drag the blue ends if the playhead is not on the cut. Press the toolbar Save button, or Send to Drive. That writes the section. Clear the flags and the same buttons write the whole path.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "trim 100 200"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "trim start"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "trim end"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "trim clear"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "trim export C:/Users/MassiveMAKE/AppData/Local/Temp/ms-trim-src"
```

Pass: `trim 100 200` logs `[trim] section 100–200`. `trim export <folder>` is the console form of the Save button with the flags set: it logs `[trim] wrote` and the file name ends in `_m100-200.src`. A LIN inside that file for a kept point matches the same LIN from a full export of that toolpath. `trim clear` logs `[trim] cleared`. After clear, Save and Send write the whole path.

Do not run `trim export` against a job the operator has not saved. When E1 motion is already on, the writer plans the rail on the full path first, the same way a full Save does, then copies those rail positions into the section.

## Gotchas

- The open app keeps the binary it launched with until quit and a rebuild.
- Mill Start, TOOL_DATA, and the cell JSON are not part of this.
- Orientation look-ahead, when it is above 0, is computed on the section alone, so the tool angles on the first and last moves of the section can differ from the full program. The XYZ, the rail, and the per-move normals still match. The default look-ahead is 0.
- A span with no print or mill move is refused and no file is written.
