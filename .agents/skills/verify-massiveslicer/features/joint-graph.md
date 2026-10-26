# Timeline joint graph

A chart button on the playback timeline draws each robot joint across the whole program on one shared chart. The chart spans the full width of the bottom timeline bar, including the area under the play controls and the readout. Each line is scaled from that joint's software stop at the bottom to the other stop at the top, so the lines overlay and the chart can use the full height. The left axis labels that shared stop range: min at the bottom, max at the top, and a percent of the range on the ticks between. When only one joint is shown, that axis switches to the joint's own unit, in degrees for A1–A6 and for a rotary E1. The bottom axis labels the move index at the left, middle, and right of the visible window. A horizontal pan can pass either end by a small margin so the first and last moves are not stuck on the border. A1–A6 are always listed. E1 is listed when the cell has a rotary bed or a rail. Click a joint name to hide or show its line. The value at the playhead sits on that joint's button. A vertical line follows the playhead. Click or drag the chart to move the playhead.

Drag the grip on top of the chart to change its height (80 to 720 pixels, starting at 200). Scroll zooms both axes around the cursor. Right-drag or middle-drag pans. Click the chart, then F restores the full timeline and the full joint range. That F is handled by the chart, so the 3D view's F still frames the selection until the chart is clicked. Home, while the pointer is on the graph, does the same restore. G stays the move shortcut. The restore leaves the height where it was dragged. The window is session-only.

The lines are the per-move joint solutions the reachability check already stored. Opening the graph does not start a new solve, does not move the robot, and does not clear realtime pause. Until that check finishes, the panel says the lines appear after the check. A value past a stop draws in red on the top or bottom edge of the chart. Each slice of the timeline keeps the lowest and highest sample in that slice, so a short trip to a stop stays visible.

E1 uses the external-axis stops on the robot panel. A rail uses the cell rail limits, in millimetres. A rotary bed uses the panel's E1 stops, in degrees.

The graph is session-only. It is not saved in the `.mass`.

## How to get to it (user POV)

Preview or SDF, with a toolpath on the playback timeline. Press the chart button on the left of the scrubber. Toggle A1–A6, and E1 when the cell has one. The chart fills the timeline bar, with min / percent / max on the left and the move index along the bottom. Drag the grip to resize. Scroll to zoom, right-drag to pan. Click the chart, then F to show the whole graph. Home does the same while the pointer is on the chart. The axis labels follow the zoomed window.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "jgraph on"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "jgraph status"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "jgraph a1"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "jgraph off"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "jgraph height 320"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "jgraph reset"
```

Pass: `jgraph on` logs `[jgraph] open`. Before the reachability check finishes, the same line says `no reachability solutions yet`. After the check, it reports the move count. `jgraph a1` logs `A1 off` or `A1` on the next toggle. `jgraph off` logs `[jgraph] closed`. `jgraph height 320` logs `height 320`. `jgraph reset` logs `view home`. A height outside 80–720 is clamped.

Do not treat `jgraph` as a motion command. `jgraph home` restores the chart window. It does not sync or jog the robot.

## Gotchas

- The open app keeps the binary it launched with until quit and a rebuild.
- F restores the zoom and pan only after the chart has been clicked. Until then F still frames the 3D selection. Home restores the chart while the pointer is on it. The dragged height stays. G remains the move shortcut.
- Scroll on the chart zooms the chart. It does not zoom the 3D view.
- The check that fills the lines is the existing reachability pass. On a multi-million-move program that pass is already running after import. The graph reads it when it finishes.
- Unwrapped wrist angles can sit outside the software stop. Those samples pin to the top or bottom of the chart in red.
- Mill Start, TOOL_DATA, and the cell JSON are not part of this.
