# Toolhead global orientation gizmo

Click the toolhead. Rotation rings appear at the TCP. Dragging a ring changes TOOLHEAD GLOBAL ORIENTATION and the tool turns with it.

## Sub-features

- The rings show on the click that selects the toolhead. They are not forced back on every frame, so the Move button can show the translate arrows.
- The X ring writes panel X (KUKA C / ToolheadC). The Y ring writes panel Y (KUKA B / ToolheadB). The Z ring writes panel Z (KUKA A / ToolheadA). Print paths use the additive sliders. Mill paths use the subtractive sliders.
- While the ring is held, only the current frame is re-posed. The full reachability pass runs once on release.
- With no toolpath loaded, the ring still turns the mounted tool. The tip stays where it was when the ring was grabbed. The joints are the preview arm.
- A ring drag does not insert a toolhead key. With no key selected it writes the global baseline. With a key selected it updates that key, the same way the sliders do.
- G still grabs the tool to move it. R, or the rotate button, brings the rings back. The translate arrows still store a TCP position offset on release.
- Degrees wrap inside −180..180.

## How to get to it (user POV)

Click the toolhead in the viewport. Drag a ring. The matching Y, X, or Z slider moves and the nozzle or cutter turns. Let go. Press G to move the tip instead. Press R to get the rings again.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "toolorient 90 0 0"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "mill y 94"
```

Pass: `toolorient 90 0 0` logs `[toolorient] Y=90 X=0 Z=0` and the tool pose matches that slider edit. On a mill path, `mill y 94` still writes the subtractive Y slider. A ring drag must not log `[tcpkey]`.

Do not run this against a job the operator has not saved. Release re-solves the open toolpath.

## Gotchas

- The open app keeps the binary it launched with until quit and a rebuild. This map was added while a window was already open.
- The rings are world axes. The pose is the same one the slider produces, so the tool can lag the ring when the offset is applied in the tool frame.
- Mill Start, TOOL_DATA, and the cell JSON are not part of this.
- G on a part that is not the toolhead is still the keyboard grab.
