# Magic scan

Push note: Scan Pose and Magic Scan move the simulated robot only. They are not connected to the live arm yet. A later update will connect those moves to the arm. These added scanning features are a work in progress and are not ready for production. Hand-eye auto-calibrate is unchanged and can still move the live arm after a sync.

Scan tab, above Capture. The button is `Magic Scan`. The dropdown is `Localization` or `Part recreation`.

Localization ends by merging the scans and starting Match model to scan. It does not generate a mill. Part recreation merges to a mesh and does not move a model. It does not build a CAD model.

Magic Scan, Move to overhead, and Go to Pose move the simulated arm only. That stays true when the robot is not synced. The fine stations are look-down positions with no joints, so Continue does not send them.

## Sub-features

- Card `MAGIC SCAN` above Capture
- Dropdown `Localization` / `Part recreation`
- Button `Magic Scan` arms the overhead confirm and does not move
- Button `Move to overhead` puts the simulated robot on the selected Scan Pose
- Coarse knobs: bed clearance, coarse field, `Detect part`
- Fine knobs: standoff, footprint, overlap, `Plan fine scan`
- Review: `New toolpath`, `Rescan`, `Continue`
- Button `Merge scans` uses the existing mesh merge
- `magic-scan status` reads mode and station count and does not move

## How to get to it (user POV)

Open the Scan tab. Magic Scan is the card above Capture. Pick Localization or Part recreation. Pick a Scan Pose that clears the part, press Magic Scan, then press Move to overhead. The simulated arm moves. Capture with Test Scan, then Detect part. If the part is wider than the coarse field, pick another saved Scan Pose and press Move to overhead again. Review the station list. New toolpath replans. Rescan returns to the overhead confirm. Continue merges and, for Localization, starts the model match.

Manual tuning stays in the same card. Capture, Scan Pose, hand-eye, and Match model to scan stay in their own cards.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "magic-scan status"
```

Pass: the line starts with `[magic-scan]` and says `viewport status only`. Do not add a console command that moves the arm. Do not press Move to overhead on a window the operator is using.

## Gotchas

- The open app keeps the binary it launched with until a restart.
- Move to overhead and Go to Pose call the viewport joint sliders. They do not call SendAxes or a waypoint move, synced or not.
- Move to overhead does not capture when Fake scan data is off. A capture while the live arm is somewhere else would stamp the simulated pose onto the photo. Use Test Scan when the camera is at that pose. With Fake scan data on, Move to overhead and Test Scan play a high rough grid and then a closer fine grid on the simulated arm only.
- A second overhead is another saved Scan Pose. It is not a computed shift of the first pose.
- Overview can hit a tall part. The pose has to clear the part before Move to overhead.
- Hand-eye is not repeated. A scan without a calibrated scanner tool is placed on the bed centre.
- Detect ignores points at or below the bed plus the clearance. Fewer than 30 points is no part.
- Two coarse clouds are boxed together. The ICP merge runs at Continue, or from Merge scans.
- Merge scans keeps a selection of two or more scans. With no selection it takes rows named `Scan ` and hides `Merged Scan`, the same rule as `scan-merge`.
- Continue does not drive the fine path. Stations have no joints, and a guessed camera orientation is not sent.
- Localization starts Match model to scan and stops. Stock, tolerance, match, shift, and turn stay on the Alignment card. A rejected match leaves the model unmoved.
- Part recreation does not call Match model to scan.
- Side sweeps and a CAD model from the scan are not in this card.
