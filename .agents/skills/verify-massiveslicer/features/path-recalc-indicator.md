# Path recalculation indicator

A spinning circle at the left of the bottom status bar while a change is rebuilding the path. The robot-validation counts stay on the right of that bar. They hide only while the next pass is running, then come back in the same place. Hover or click shows the full sentence.

## Sub-features

- The circle is on for a slice, a toolhead-keyframe rebake, and the reachability pass that follows a path change.
- It is painted before the keyframe rebuild, so a freeze of the window lines up with the circle.
- The circle stays up through the toolpath upload that the rebuild enqueues, and through the reachability pass.
- A burst of rebakes holds the circle once. It turns off when the newest rebake's upload has drawn, unless the reachability pass is still running. An earlier rebake in that burst cannot leave the circle on.
- No label and no percent. The slice percent row is unchanged.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "viewset IsValidating true"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py screenshot
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "viewset IsValidating false"
```

Pass: the screenshot shows the lime circle spinning at the left of the bottom status bar while `IsValidating` is true, the validation counts are hidden for that time, and `viewset IsValidating` reads false afterward. After the pass, the counts are back on the right: singularity, unreachable, and collisions. Hover or click shows the full sentence. The line is not moved.

Do not drive a real rebake (`tcpkey add`, `mill y`) against an open mill job. That re-solves the toolpath.
