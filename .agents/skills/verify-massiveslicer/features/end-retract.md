# End retract

A print can finish with one travel straight up in world Z. The extruder is off. The distance is set in MOVEMENT.

## Sub-features

- Checkbox `End retract` in the print MOVEMENT section, under Z hop
- Distance `End retract (mm)`, default 50
- The move is world +Z from the last point, including after a wipe
- Off leaves the path unchanged

## How to get to it (user POV)

Right panel, print settings, MOVEMENT. Check End retract and set the distance. Slice.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset EndRetractEnabled"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset EndRetractDistanceMm"
```

Pass: the read prints `False` and `50` on a fresh panel. Setting a value reslices. Do not send a value against a job the operator has not saved.

## Gotchas

- The open app keeps the binary it launched with until a restart. The checkbox is not in that process.
- The lift is world Z, not the last bead direction and not the layer normal.
- A saved `.mass` and a movement preset store the checkbox and the distance. Older files stay off, with the distance at 50.
