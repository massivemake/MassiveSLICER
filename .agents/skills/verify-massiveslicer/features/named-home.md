# Named home

SAVE AS HOME POSITION stores a named joint pose. Export start PTP uses **SelectedHomeAngles**, not the cell default, until that name is selected.

## Sub-features

- List/select a named home in the UI
- Save as home (operator)
- Export start PTP matches the **selected** name
- LFAM 3 Mill Start joints stay `0/−90/90/0/45/105` — not a home rewrite

## How to get to it (user POV)

ROBOT / home dropdown. Save as home. Select the name before export.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py status
```

Pass: `/status` returns pose + tool + base. Quote those numbers.

**Do not** run `home`, `home go`, or `home save` from verify unless the user explicitly asked to teach or move. Bare `home` **moves** via MassiveDRIVE.

For an export task: prove the SRC opening PTP joints match the selected home (and Mill Start was not overwritten). Persist name+joints on `.mass`.

## Gotchas

- SAVE AS HOME is not the export PTP until the name is selected. Cell `defaultHomePosition` stays Home if save never selected.
- Cartesian LIN to a live C −135 park spins the wrist. Park is joints PTP.
- `connected: true` → stop and ask before any home command.
