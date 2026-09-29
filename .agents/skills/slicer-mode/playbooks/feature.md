# Playbook: feature

New or changed user-visible behavior. Name the **data shape** first.

1. Architect: who owns the setting (`.mass` / `UiSession` / cell JSON / mill sidebar). Three `lfam3.json` copies if cell.
2. Blast-radius: persist/restore, export, Drive Send, console command.
3. Smallest complete version. YAGNI — no second mill catalog, no extra gizmo.
4. Console command if an agent will need to drive it later; add a `verify-massiveslicer/features/*.md` file in the same change.
5. TDD for Core math. Running app for viewport.
6. Verify the new feature map file (doctor + drive).
7. `memory.md` changelog (past) + `ROADMAP.md` if it was backlog. Commit docs with the code.
8. Branch if it hits Viewport giants / motion / `.mass` schema. Offer `feature/<name>` before editing.
