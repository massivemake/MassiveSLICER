# Scan pose

Push note: Scan Pose and Magic Scan move the simulated robot only. They are not connected to the live arm yet. A later update will connect those moves to the arm. These added scanning features are a work in progress and are not ready for production.

Scan tab → Capture → Scan Pose. A named joint pose for the simulated robot. Overview is the scanner-down pose over the bed. Go to Pose puts the viewport arm on the selected joints. It does not move the live arm, including while a program is running.

Poses are stored in `*.scanposes.json` beside the active cell JSON. Saving does not rewrite the cell, home positions, or the workspace.

## Sub-features

- Section `SCAN POSE` in Capture
- Dropdown of saved poses, including Overview
- `Go to Pose` moves the simulated robot only
- Name box and `SAVE AS SCAN POSE` store the current simulated A1–E1
- `scan-pose status` lists names and does not move anything
- `scan-pose go [name]` moves the viewport robot
- `scan-pose save [name]` saves the simulated joints

## How to get to it (user POV)

Open the Scan tab. Capture is the first card. Scan Pose is under the capture blurb. Pick a pose and press Go to Pose. Type a name and press SAVE AS SCAN POSE to keep the simulated sliders.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "scan-pose status"
```

Pass: the line lists Overview and says `viewport only`. Do not run `scan-pose go` on a window the operator is using. That command moves the simulated arm in that window. It does not call MassiveDRIVE.

`scan-pose save` writes `*.scanposes.json` next to the cell the window has loaded. It reads the simulated sliders, not the live robot.

## Gotchas

- The open app keeps the binary it launched with until a restart.
- Quick Positions stay hidden. Those buttons move the live arm. Scan Pose does not use them.
- Picking a name in the dropdown does not move the arm. Go to Pose does.
- Overview starts as the scanner-down-bed joints. Saving the name Overview replaces those joints in the sidecar.
- A copy of `lfam3.json` does not erase saved poses. The sidecar is the copy that lasts.
- Go to Pose pauses the inbound joint poll so the next live reading does not snap the viewport arm back. It does not stop a program on the controller.
