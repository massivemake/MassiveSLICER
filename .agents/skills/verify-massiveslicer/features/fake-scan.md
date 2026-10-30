# Fake scan

Scan tab. The checkbox is `Fake scan data`. It starts off.

Off: Test Scan uses the Zivid camera. On: Test Scan lifts the simulated scanner above the whole model, collects a rough grid, then moves closer for the fine points. It does not stop on the taught overhead pose, and it does not jump straight to the point cloud. The live arm is not moved. The same checkbox is on the Magic Scan card and in Capture → Camera. Hand-eye and bed calibration always use the camera. The console `scan` command always uses the camera.

Push note: Scan Pose and Magic Scan move the simulated robot only. They are not connected to the live arm yet. A later update will connect those moves to the arm. These added scanning features are a work in progress and are not ready for production. Fake scan data is a stand-in cloud for that simulation.

## Sub-features

- Checkbox `Fake scan data`, default off
- Caption that says whether the camera will be used
- Test Scan, when the box is on, adds a scan named `Scan ` from the model
- `scan-fake status` reads the toggle and does not capture
- `scan-fake on` and `scan-fake off` flip the toggle and do not capture

## How to get to it (user POV)

Open the Scan tab. The checkbox is at the top of Magic Scan and again above Test Scan. Leave it off for a real capture. Turn it on, pick a Scan Pose, then press Test Scan or Move to overhead. The simulated arm lifts clear of the part and visits a grid that sketches the whole object, then comes down for the closer points. Go to Pose still jumps the simulated arm and does not collect.

A selected model is the one that is scanned. Otherwise the visible model is used. A hidden model is used when no model is visible. Existing scans are not copied.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "scan-fake status"
```

Pass: the line is `[scan-fake] off` and says the Zivid camera is used. `scan-fake on` only flips the session toggle. It does not capture and does not move the arm. Turn it off again when finished.

## Gotchas

- The flag is session state. It is not saved in the workspace, so a reopened file starts off.
- Test Scan is the only capture that checks the flag. Hand-eye, bed calibration, and the console `scan` command still open the camera.
- The fake cloud is registered with the camera that took it. With the toggle on, that camera is the high grid and then the closer grid, not the taught overhead height. Go to Pose still only moves the simulated arm.
- A face the camera cannot see is left out. A miss says no frame saw the model.
- If the simulated arm cannot hold the high grid, the rough frames are still taken from cameras above the model and the status says so. The live arm is not moved.
- The added row is named `Scan` so Merge scans can include it. The status line starts with FAKE.
- The open app keeps the binary it launched with until a restart.
