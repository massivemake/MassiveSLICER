# T1 TCP outer envelope

LFAM 3 Tool #1 HV kinematic hull in the viewport. Same solid as Drive `02_T1_HV_TCP_outer`. Visualization only — not collision, mill, or IK.

## Sub-features

- VIEWPORT → VISIBILITY → **T1 TCP outer** checkbox (default off)
- On: lime 5% ghost, seated at ROBROOT
- Off: hidden
- `viewset ShowT1TcpEnvelope true|false`

## How to get to it (user POV)

Load LFAM 3. Left VIEWPORT → VISIBILITY → T1 TCP outer. Envelope should sit around the robot with the curtain / beds visible through it.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py console --cmd "viewset ShowT1TcpEnvelope true"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py screenshot --out "$HOME/Library/Application Support/MassiveSlicer/verify-evidence/t1-tcp-outer.png"
```

Pass: screenshot shows a faint lime volume around the KR 120; beds and toolpath still read through it. Uncheck / `viewset ShowT1TcpEnvelope false` hides it. Do **not** treat it as a pad `$WORKSPACE` cuboid or a collision mesh.

## Gotchas

- Mesh is `assets/cells/LFAM3/t1_hv_tcp_envelope.glb` from TOOL_DATA[1] FK, not the flange hull.
- Default off so it does not clutter Body/Toolpath views.
- Authoring overlay: skipped by collision, cavity, contact shadows, and scene bounds.
