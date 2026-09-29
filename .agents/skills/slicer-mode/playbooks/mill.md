# Playbook: mill

MILL tab catalog + T12. Load `massiveslicer` mill refs (`mill-ops`, `t12-mill-ik`, `t12-drive-overlay`).

1. Architect if touching `MillPlanner`, mill IK, or `ViewportView.axaml.cs`.
2. Blast-radius: overlay vs IK vs Mill Start vs `lfam3.json` (three copies) vs Drive mill Send.
3. Never mill HeatedBed. User part or SVG selected (`PickTier.Environment` skip).
4. Do **not** change Mill Start joints `0/−90/90/0/45/105` or pad TOOL_DATA to fit a picture.
5. Implement the smallest op/path change. Console: `mill status` / `mill op` / `mill axis`.
6. Verify: `verify-massiveslicer` feature `mill-generate` (and `t12-overlay` if triad involved).
7. Shop Release `--no-incremental` before claiming a visual/IK fix. Wait for confirm before commit.
8. Mixed dirty tree: do **not** `save.sh` (`add -A`). Stage mill files only.
