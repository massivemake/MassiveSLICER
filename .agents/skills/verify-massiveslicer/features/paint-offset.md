# 2D Offset (paint edit)

Edit-mode Offset is a `PathOffsetSpec` (distance / side / layers up+down), spliced as **wall extrudes** after the source contour. Two Applies on different layers = two specs.

## Sub-features

- Offset on current scrub layer
- Layers up / down
- Second Apply on another layer keeps both
- Update Slice bakes walls into KRL/Drive
- Empty Edit sidebar: cards hidden, not collapsed

## How to get to it (user POV)

Slice a part. Preview/Edit. Select a contour. Offset → Apply. Change layer, Apply again if needed. Update Slice.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "outliner-tree"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py screenshot --out "$HOME/Library/Application Support/MassiveSlicer/verify-evidence/offset.png"
```

Pass: outliner still has the user mesh (not only bed). After an Offset+Update Slice session, extra wall beads exist on the intended layers and survive a second Update Slice.

This feature often needs the GUI (layer scrub + Apply). If you cannot drive Apply from console, say so and use screenshot + outliner as partial proof — do not invent a console Offset command.

## Gotchas

- Offset is not a live-layer insert; leftover selection from layer 14 used to steal layer-15 Apply.
- Two Offsets on different layers must remain two specs.
- `select NAME --toolpath` is empty when the PrintToolpath is a **sibling** on the bed, not a child. Use `outliner-tree`.
