# Feature map

User-facing paths the next agent can drive cold. Each file: what it is, how a user gets there, how to drive it on the bridge, gotchas.

Start with these five. Add a file when a new user-visible behavior ships.

| Feature | File | Cheap read-only probe |
|---|---|---|
| Mill generate | `mill-generate.md` | `mill status` |
| T12 overlay | `t12-overlay.md` | `/status` tool + screenshot |
| T1 TCP outer | `t1-tcp-envelope.md` | `viewset ShowT1TcpEnvelope` |
| KRL post header | `krl-export-header.md` | `krlpost` |
| Named home | `named-home.md` | `/status` pose — **do not** `home` |
| 2D Offset | `paint-offset.md` | `outliner-tree` after a slice |
| Export polyline | `export-polyline.md` | `export-polyline /tmp/slicer-polyline.obj` |
| Angled walls | `angled-walls.md` | `addset WallCount` (read only; a value reslices) |
| Wipe 90-Degree | `wipe-90.md` | `addset WipeModeDisplay` (read only; a value reslices) |
| TCP Adjust | `tcp-adjust.md` | `tcpadj` (list is read-only; `add` places a handle) |
| Toolhead keyframe | `tcp-keyframe-orient.md` | `tcpkey add` (needs a preview timeline; re-solves the open path) |
| Signed Distance | `signed-distance.md` | `sdf` (read-only; does not select the method) |
| Continuous Planar Field | `continuous-planar.md` | `cpf` (read-only; does not select the method) |

Proof standards: real user path, action + resulting state, side effects on disk/console. Unit tests are extra, not a substitute.
