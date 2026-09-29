# KRL export header / Rules

Export → Post-Processing → Rules: **Robot Mode** (MAT/RPM) and **Travel Moves** (`RPM = 0` + `T1 = 180` at `;travel start`) are two independent buttons.

## Sub-features

- Robot Mode on/off
- Travel Moves on/off
- Both on together (shop default)
- Header/footer length; factory file `assets/krl_postprocess.json`
- Empty header that still has `;FOLD Safety` is valid (do not require the string `CaracolSafety`)

## How to get to it (user POV)

Export → Post-Processing → Rules. Toggle Robot Mode / Travel Moves. Done writes the factory JSON.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "krlpost"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "krlpost robot on"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "krlpost travel on"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "krlpost"
```

Pass: two `krlpost` reports. After the ons, `Robot Mode: ON` and `Travel Moves (start/stop): ON`. Do not merge them into one checkbox in code or UI.

Export-file proof (when the task is an SRC change): a real `.src` contains `;travel start` then `RPM = 0` / `T1 = 180`, and no cartesian `PTP {X Y Z}` without ABC. Comments ASCII hyphen only — never `T1`/`HALT`/`$ANOUT` in comments.

## Gotchas

- `krlpost` with no args is a report (safe). `home` is not.
- Opening Done bumps `assets/krl_postprocess.json` `UpdatedAtUtc` even when Rules did not change — unstage timestamp-only diffs.
- PointLoader Load, never pad Select, for production SRC.
