# Blast-radius (Slicer)

A small-looking change. Prove what else it breaks **by running code**, not by asserting isolation.

## Coupled systems (check the ones you touch)

| You touched | Also re-verify |
|---|---|
| T12 overlay position | mill IK bit-tip, FLANGE at A6, Mill Start joints unchanged |
| Mill TOOLHEAD Y/X/Z | live tool 11 vs 12 ABC (no T12 fallback when T11 mounted) |
| SRC travel / RPM | PointLoader Load, CadCommands 29, comments, LFAM 2 E1 |
| Wipe / island hop | Drive Send stitch, wipe From = last extrude To |
| Cell JSON TCP | all three `lfam3.json` copies |
| ComboBox theme | TOOL # pixel is lime, not `#642d55`, after shop Release |
| Named home | export PTP = selected name; Mill Start not overwritten |
| 2D Offset | two specs on two layers; Update Slice while slicing |

## Proof

One command that would go red if you broke the neighbor (`mill status`, `krlpost`, a unit test, UTF-16 `strings` check on the DLL). Run it.

Safe because: `<that command's output>`. Not safe because: “this file is unrelated.”
