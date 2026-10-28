# Feature map

User-facing paths the next agent can drive cold. Each file: what it is, how a user gets there, how to drive it on the bridge, gotchas.

Start with these five. Add a file when a new user-visible behavior ships.

| Feature | File | Cheap read-only probe |
|---|---|---|
| Mill generate | `mill-generate.md` | `mill status` |
| Drill holes | `drill-holes.md` | `mill drill` |
| T12 overlay | `t12-overlay.md` | `/status` tool + screenshot |
| T1 TCP outer | `t1-tcp-envelope.md` | `viewset ShowT1TcpEnvelope` |
| KRL post header | `krl-export-header.md` | `krlpost` |
| Named home | `named-home.md` | `/status` pose — **do not** `home` |
| 2D Offset | `paint-offset.md` | `outliner-tree` after a slice |
| Export polyline | `export-polyline.md` | `export-polyline /tmp/slicer-polyline.obj` |
| Angled walls | `angled-walls.md` | `addset WallCount` (read only; a value reslices) |
| Wipe 90-Degree | `wipe-90.md` | `addset WipeModeDisplay` (read only; a value reslices) |
| End retract | `end-retract.md` | `addset EndRetractEnabled` (read only; a value reslices) |
| TCP Adjust | `tcp-adjust.md` | `tcpadj` (list is read-only; `add` / `deflect` place a handle) |
| Outliner group | `outliner-group.md` | `group` (show is read-only; `move` / `yaw` change the scene — undo) |
| Toolhead keyframe | `tcp-keyframe-orient.md` | `tcpkey add` (needs a preview timeline; re-solves the open path) |
| Timeline section | `timeline-trim.md` | `trim 100 200` (marks only; Save, Send, and `trim export` write that span) |
| Timeline joint graph | `joint-graph.md` | `jgraph status` (read-only; `jgraph on` opens the panel, `jgraph reset` restores the chart window, and neither solves nor moves) |
| Motion and extruder RPM | `motion-rpm.md` | `scrub` (read-only; the following line is the move's mm/s and RPM %. `addset PrintSpeed` writes the panel — put it back) |
| Signed Distance | `signed-distance.md` | `sdf` (read-only; does not select the method) |
| SDF bit side carve | `sdf-bit-side-carve.md` | `viewset SdfCarveBitSides` (read-only; `true` rebuilds the SDF view) |
| Continuous Planar Field | `continuous-planar.md` | `cpf` (read-only; does not select the method) |

Proof standards: real user path, action + resulting state, side effects on disk/console. Unit tests are extra, not a substitute.
