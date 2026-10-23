# Outliner group select

Shift-click or Ctrl-click in the outliner selects several objects. The move, rotate, and scale handles then treat that selection as one group. The handle sits at the centre of the combined bounds and uses world axes. Every selected object takes the same move. Rotate and scale happen about that shared centre, so the objects stay in the same arrangement. A child of a selected parent is not moved on its own. A model's toolpath comes along for a move or a rotate. A scale leaves the toolpath and re-slices the selected model when the handle is released. Toolpath rows still use Shift to pick a print sequence. The robot, the toolhead, and cell fixtures are not part of the group. The selection is not saved.

## Sub-features

- Shift selects the range of groupable rows between the anchor and the click. Ctrl adds or removes one row
- One remaining row falls back to ordinary single selection
- The lime outline is on every selected object
- Translate, rotate, and scale use one shared world change. Undo restores every member

## How to get to it (user POV)

Outliner. Click one model, scan, attractor, or modifier. Shift-click another. Drag the move, rotate, or scale handle.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "select attractor1"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "group ctrl attractor2"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "group"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "group move 0 20 0"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "undo"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "group yaw 15"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "undo"
```

Pass: `group` after `select` plus `group ctrl` prints two names and one `pivot=`. `group move 0 20 0` prints the same Y delta on both lines. `undo` puts both positions back. `group yaw` prints two new positions around the same pivot, and `undo` restores them. Do not `group move` on a job the operator has not saved. Move back, or undo, before leaving the scene.

## Gotchas

- Shift on a toolpath row still selects a print sequence. It does not join the transform group.
- Scale re-slices the model that was clicked last. Other models in the group keep their old toolpaths until they are sliced again.
- The open app keeps the binary it launched with until a restart.
