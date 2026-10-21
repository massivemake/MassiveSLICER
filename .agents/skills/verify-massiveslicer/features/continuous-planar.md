# Continuous Planar Field

Planar slicing method from Cocco and Chermain, Wave-Guided Field-Aligned Volume-Filling Curves (SGP 2026). Each mesh island keeps its outline. The wall and any hole outline are printed once. Infill lines cover the closed area. Each new line is the closest unused one, and a gap within 3× bead width is an extruded stitch, the same nearest-entry rule as toolpath optimize. A bead already printed on the layer is not printed again, and no bead crosses another. Islands are not unioned. A lift separates a line that cannot be stitched without a cross.

## Sub-features

- SLICING METHOD combo gains `Continuous Planar Field`
- Each island's wall, and each hole outline, is printed once. The path does not return over a bead already printed on that layer
- Infill lines cover the closed area. The next line is the closest unused one
- A stitch within 3× bead width is extruded when it stays inside the island and does not cross or ride an existing bead
- Bead width is the curve spacing. Layer height is the planar stack

## How to get to it (user POV)

Right panel, step 2 SLICE, open SLICING METHOD, choose Continuous Planar Field. Slice.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "cpf"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset MethodDisplayName"
```

Pass: `cpf` prints `listed=True` and the active method name. `addset MethodDisplayName` with no value prints the current method.

Do not pass a value to `addset MethodDisplayName`. A value selects the method and reslices the open job.

## Gotchas

- The open app keeps the binary it launched with until a restart.
- Infill pattern, wall count, and planar surface mode do not apply. The method builds its own wall and fill.
- A segment that would cross a hole, cross another bead, or ride a bead already printed is left out. Extra pieces are reached by a lift, so the across move is not on the layer.
- The climb to the next layer starts on the outline nearest the previous end, so it stays in the solid.
- Islands stay separate. The lift between them is not a printed bridge.
