# Timeline toolhead orientation keyframes

Create Keyframe on the preview timeline stores the toolhead global orientation (Y / X / Z). Moving those sliders while the key is selected saves the new angles on that key. Playback eases from one key into the next. Past the last key, and before the first, it eases back to the orientation the sliders had when the first key was created.

## Sub-features

- The key button works without a TCP drag. A drag still stores a position offset.
- Y is KUKA B, X is KUKA C, Z is KUKA A. Same sliders as TOOLHEAD GLOBAL ORIENTATION.
- The selected key is what the sliders edit. Other keys keep the angles they stored. A key's green window holds that key's angle. The blank gap between windows still eases from one key to the next. Where two windows overlap, those two angles blend.
- With no key selected, the toolhead Y/X/Z sliders follow that blend as the timeline moves, including playback. A change there saves a key at the playhead. Two keys at −90° and +90° show 0° halfway between them; setting that spot to 10° stores a key at 10°. A key already on that move takes the new angle. Before the first key the sliders ease from the orientation saved with that key at the start of the path up to the key. After the last key they ease from the key back to that orientation at the end of the path. A half-turn from a negative angle to a positive one passes through 0, including a fraction past 180°. A shorter step across ±180, such as 170° to −170°, still takes that short step.
- The X Y Z boxes under the lane are the selected key's position offset in millimetres. Editing them moves that key only. Dragging the tool saves a new offset on the key at the playhead and does not copy another key's angle onto it.
- Scrub and play use the eased angle. KRL and Drive export read it off each move.
- Clear restores the unkeyed orientation on the sliders.
- The key-minus button, the Delete key, and `tcpkey delete` remove the selected key. The last key restores the unkeyed path. A click on the selected diamond clears that selection and leaves the playhead where it is. Selecting another scene item, such as an attractor, clears that key selection so Delete removes the item. The active toolpath and the current tool keep the key.
- KEYFRAMES sits at the front of the caption line, before Unreachable, Singularity risk, and Collision. Closing the chevron hides the diamond row, the lime ticks, and the offset boxes, so the timeline is shorter. The keys stay on the path. Adding a key opens the lane again.
- Shift-drag moves a key along the lane. The scrubber follows that move. A plain drag on a tail still sizes that key's ease. The saved angles and the position offset stay on the key.

## How to get to it (user POV)

Preview timeline, Create Keyframe, then move Toolhead global orientation Y, X, or Z. Play. The spindle eases into that angle and back out, or into the next key if you add one.

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
- Export uses the eased angle on each move. With a key selected, the slider number stays on that key while the timeline plays. With none selected, the sliders follow the blend.
- Changing Y/X/Z while the path has no keyframes still tilts the whole path, including mill play. Once keys exist, a change with none selected adds a key at the playhead.
