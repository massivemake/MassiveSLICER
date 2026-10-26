# Timeline toolhead orientation keyframes

Create Keyframe on the preview timeline stores the toolhead global orientation (Y / X / Z). Moving those sliders while the key is selected saves the new angles on that key. Each key falls off to the baseline inside its influence window, and the tail is the baseline again. A gap that no window covers stays on the baseline. Overlapping windows blend.

## Sub-features

- The key button works without a TCP drag. A drag still stores a position offset.
- Y is KUKA B, X is KUKA C, Z is KUKA A. Same sliders as TOOLHEAD GLOBAL ORIENTATION.
- The selected key is what the sliders edit. Other keys keep the angles they stored. A key's green window falls off from that key's angle to the baseline at each tail. A blank gap stays on the baseline. Where two windows overlap, those two angles blend, and that blend still fades to the baseline by the combined weight.
- With no key selected, the toolhead Y/X/Z sliders stay on the global baseline, including during playback. Changing them edits that baseline and does not add a key. A half-turn from a negative angle to a positive one passes through 0 where the windows overlap, including a fraction past 180°. A shorter step across ±180, such as 170° to −170°, still takes that short step.
- The X Y Z boxes under the lane are the selected key's position offset in millimetres. Editing them moves that key only. Dragging the tool saves a new offset on the key at the playhead and does not copy another key's angle onto it.
- Scrub and play use the eased angle. KRL and Drive export read it off each move.
- Clear restores the unkeyed orientation on the sliders.
- The key-minus button, the Delete key, and `tcpkey delete` remove the selected key. The last key restores the unkeyed path. A click on the selected diamond clears that selection and leaves the playhead where it is. A click on a different diamond, or on a tail, selects that key and jumps the timeline to the key. Selecting another scene item, such as an attractor, clears that key selection so Delete removes the item. The active toolpath and the current tool keep the key.
- KEYFRAMES sits at the front of the caption line, before Unreachable, Singularity risk, and Collision. Closing the chevron hides the diamond row, the lime ticks, and the offset boxes, so the timeline is shorter. The keys stay on the path. Adding a key opens the lane again.
- Ctrl-drag moves a key along the lane. The scrubber follows that move. Shift-drag on a tail sizes that key's ease, and the playhead jumps to the key. A plain click selects and jumps. The saved angles and the position offset stay on the key.

## How to get to it (user POV)

Preview timeline, Create Keyframe, then move Toolhead global orientation Y, X, or Z. Play. The spindle holds that angle on the key and is back to the baseline at each tail. A gap between windows stays on the baseline. Overlapping windows blend.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpkey add"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "mill y 94"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpkey delete"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpkey clear"
```

Pass: `tcpkey add` logs `[tcpkey] add at move` and the viewport logs `[tcpkey] move N orient Y=…`. `mill y 94` after that stores Y=94 on the selected key. The robot at that move uses it. Moves outside the key's window use the orientation from before the edit. `tcpkey delete` removes that selected key. `tcpkey delete` with nothing selected logs `no keyframe selected`.

Do not run this against a job the operator has not saved. It re-solves the open toolpath.

## Gotchas

- The open app keeps the binary it launched with until quit and `bash run.sh`.
- Mill Start, TOOL_DATA, and the cell JSON are not part of this.
- Export uses the eased angle on each move. With a key selected, the slider number stays on that key while the timeline plays. With none selected, the sliders stay on the baseline.
- Changing Y/X/Z while the path has no keyframes still tilts the whole path, including mill play. Once keys exist, a change with none selected edits the baseline and does not add a key.
