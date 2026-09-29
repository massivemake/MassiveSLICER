# Playbook: krl

SRC / PointLoader / Travel Moves / Drive Send. Load `krl-export-header`, `krl-approach-ptp`, `pointloader-src`.

1. Inspect the **file that will run** (often `D:`), not only the exporter source.
2. Architect the dialect: `;travel start` / `WAIT SEC 0` / `RPM = 0` / `T1 = 180`. Two Rules buttons stay independent.
3. Blast-radius: header vs CadCommands 29 vs PointLoader 61/57 vs Drive stitch/wipe.
4. Never emit cartesian `PTP {X Y Z}` without ABC. Never `T1`/`HALT`/`$ANOUT` in comments. ASCII hyphen only.
5. LFAM 2: no dummy `E1 0.000`. LFAM 3 rotary is E1, not E2. First-layer Z is BASE (~3), not `$POS_ACT` 919.
6. Tight loop: export or grep the SRC; `krlpost` on the bridge; PointLoader Load on shop — not pad Select.
7. Verify feature `krl-export-header`. Rebuild shop before they Load a new Rev.
8. Confirm on cell before `save.sh`. Do not rewrite the playing SRC on the controller as the “fix.”
