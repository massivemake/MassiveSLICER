# Playbook: bug

Copy these into the todo list. Do not skip to a patch.

1. Quote the symptom (pad code, screenshot, console line). Do not rephrase away numbers.
2. Tight loop: one command that goes red on **this** symptom (`dotnet test` filter, bridge `cmd`, or a known SRC Load). Run it once.
3. `systematic-debugging` Phase 1 — root cause before fixes. No “try X.”
4. If Core math: failing test first (`test-driven-development`). If UI/IK: running app + `verify-massiveslicer`.
5. One fix at the source. No bundled refactor.
6. Re-run the tight loop. Then doctor + one mapped feature.
7. If 3 fixes failed: stop and question architecture with the user.
8. Do not commit mill/IK/KRL until they confirm on the machine. Then `bash save.sh` (Mac) / `.\save.ps1` (shop).
