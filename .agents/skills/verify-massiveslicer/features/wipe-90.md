# Wipe 90-Degree

WIPE mode that leaves the bead at 90° in the layer plane. Of the two sides, it picks the one toward the closer of the previous bead and the next bead. If neither sits beside the bead, it leans toward the travel.

## Sub-features

- Combo adds `90-Degree` next to Off, Retrace, and Same-Direction
- The wipe stays in the layer plane, including angled slices
- Length, ramp, speed, and short-travel skip are unchanged
- Shop recipe on a fresh import is still Same-Direction

## How to get to it (user POV)

Right panel, WIPE, open the mode combo, choose 90-Degree. Slice.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset WipeModeDisplay"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset WipeModeDisplay 90-Degree"
```

Pass: `addset WipeModeDisplay` prints `90-Degree`. A following slice shows wipe moves whose direction is perpendicular to the last bead and points at the nearer neighbor.

`addset` writes the property and the realtime slice watch reslices. Do not send a value against a job the operator has not saved. Read-only `addset WipeModeDisplay` is safe.

## Gotchas

- The open app keeps the binary it launched with until a restart. The new combo entry is not in that process.
- A wipe that would finish below the cell bed (LFAM 2 is Z = 130, not Z = 0) takes another heading. The Z smash stops at that same bed.
- A saved `.mass` or preset stores the display string. Older files stay on whatever mode they had.
- Negative ramp still dips in world −Z before the sideways wipe.
