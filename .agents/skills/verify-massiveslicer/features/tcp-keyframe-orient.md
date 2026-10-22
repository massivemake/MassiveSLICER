# Timeline toolhead orientation keyframes

Create Keyframe on the preview timeline stores the toolhead global orientation (Y / X / Z). Moving those sliders while the key is selected saves the new angles on that key. Playback eases from one key into the next. Past the last key, and before the first, it eases back to the orientation the sliders had when the first key was created.

## Sub-features

- The key button works without a TCP drag. A drag still stores a position offset.
- Y is KUKA B, X is KUKA C, Z is KUKA A. Same sliders as TOOLHEAD GLOBAL ORIENTATION.
- The selected key is what the sliders edit. Other keys keep the angles they stored.
- Scrub and play use the eased angle. KRL and Drive export read it off each move.
- Clear restores the unkeyed orientation on the sliders.

## How to get to it (user POV)

Preview timeline, Create Keyframe, then move Toolhead global orientation Y, X, or Z. Play. The spindle eases into that angle and back out, or into the next key if you add one.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpkey add"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "mill y 94"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "tcpkey clear"
```

Pass: `tcpkey add` logs `[tcpkey] add at move` and the viewport logs `[tcpkey] move N orient Y=…`. `mill y 94` after that stores Y=94 on the selected key. The robot at that move uses it. Moves outside the key's window use the orientation from before the edit.

Do not run this against a job the operator has not saved. It re-solves the open toolpath.

## Gotchas

- The open app keeps the binary it launched with until quit and `bash run.sh`.
- Mill Start, TOOL_DATA, and the cell JSON are not part of this.
- Export uses the eased angle on each move. The slider number stays on the selected key while the timeline plays.
- Changing Y/X/Z with no keyframe still tilts the whole path, including mill play.
