# Workspace new / open / save

Workspace commands create an empty scene, open a `.mass` file, or save the current document through the in-app console (relayed by the control bridge).

## Sub-features

- `workspace-new` starts an empty workspace (reloads cell, clears user scene).
- `workspace-open` opens a `.mass` path when provided.
- `workspace-save` / `workspace-save-as` persist the workspace when a path applies.

## How to get to it (user POV)

- Console: `new`, `open [path]`, `save`, `save-as [path]`.
- File menu / chrome equivalents (same MainWindowViewModel APIs).

## Driving it with control-massiveslicer

Preconditions:

- `doctor` is ok.
- You may overwrite the current unsaved scene with `new`.

- **New workspace.** Run `... command new`. Response `ok: true`. Then `... console -N 20` and expect `[workspace] New workspace`.
- **List commands.** Run `... command help`. Response includes `new`, `open`, `save`.
- **Open (agent-safe).** Prefer `... command open "<absolute-.mass>"` for arbitrary paths. Helper `open` / `POST /open` only accepts MassiveFILES paths (`Z:\…`).
- **Save.** `command save` writes only when a current path (or ERP docs folder) applies; otherwise it opens a Save As picker agents cannot complete — use `command save-as "<path>"` instead.
- **Proof.** Screenshot `artifacts/workspace/after-new.png` plus the `new` and console JSON. HTTP `ok` alone is not enough if a picker was opened.

## Gotchas

- Pathless `open` / `save-as` open native file pickers — agents cannot finish those.
- `new` discards unsaved work. Confirm before using it on a live user session.
- Bridge helper `open` rejects non-MassiveFILES paths; use `command open` for other drives.
