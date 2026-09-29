# Architect (Slicer)

Settle caller usage, types, and owner file **before** editing a boundary. Complements Hermes `plan` (plan writes markdown; this refuses to start code).

## When

Change crosses a function or module boundary, especially:

- `ViewportView.axaml.cs` / `ViewportViewModel.cs`
- Mill IK / overlay / `MillPlanner`
- KRL exporter / PointLoader dialect
- Cell JSON (`assets/cells/LFAM3/lfam3.json` **and** the two copies)

## Do

1. Name the owner type and the one file that should change first.
2. Name the live authority (pad TOOL_DATA, Mill Start joints, selected home, BASE frame) — measurements vs something to rewrite.
3. List what you will **not** touch (Mill Start, TOOL_DATA XYZ, Drive `bed_origin` bake, WV Transfer).
4. If two designs are plausible, write both in 5 lines and pick the smaller.

## Don't

- Import robotics libraries (`rl::mdl`) or a second mill CAM.
- “Fix” overlay by editing `lfam3.json` or park joints.
- Start `ViewportView.axaml.cs` until the shape is written.
