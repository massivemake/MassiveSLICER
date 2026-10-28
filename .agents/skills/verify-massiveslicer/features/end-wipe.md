# End wipe

The last part of a print can run with the extruder already off. The nozzle stays on the path, so pressure left in the screw comes out on the bead instead of blobbing after the stop. End retract, when it is also on, lifts after that dry tail.

## Sub-features

- Checkbox `End wipe` in print MOVEMENT, under End retract
- Distance `End wipe (mm)`, default 50, clamped 0–2000
- Off leaves the path unchanged
- The dry distance is measured along extrusion only. A travel in the tail does not use it up
- Both on: dry tail, then one travel straight up by the end-retract distance

## How to get to it (user POV)

Right panel, print settings, MOVEMENT. Check End wipe and set the distance. Slice. Check End retract as well when the nozzle should lift after the wipe.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset EndWipeEnabled"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset EndWipeDistanceMm"
```

Pass: a fresh panel reads `False` and `50`. Setting a value reslices. Do not send a value against a job the operator has not saved.

## Gotchas

- The open app keeps the binary it launched with until a restart. The checkbox is not in that process.
- The dry tail is drawn with the wipe color and is not a deposited bead.
- Export writes `;end wipe`, turns the extruder off, and keeps print speed. It does not use the wipe speed or the wipe suck-back flag.
- A saved movement preset stores the checkbox and the distance. Older files stay off, with the distance at 50.
