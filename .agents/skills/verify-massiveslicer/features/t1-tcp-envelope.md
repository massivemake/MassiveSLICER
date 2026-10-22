# T1 TCP outer envelope

LFAM 3 Tool #1 HV kinematic hull in the viewport. Same solid as Drive `02_T1_HV_TCP_outer`. Visualization only — not collision, mill, or IK.

## Sub-features

- VIEWPORT → VISIBILITY → **T1 TCP outer** checkbox (default off)
- On: lime ghost, seated at ROBROOT, double-sided so it reads from inside the hull
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

- Mesh is `assets/cells/LFAM3/lfam3_flangeEnvelope.glb`, flange kinematic hull in ROBROOT metres. Not the old Tool #1 TCP outer.
- The hull starts hidden. Cell GPU upload skips invisible nodes, so the checkbox must queue that upload or the mesh never appears.
- The camera sits inside the hull. `CullFaces` must stay off or every wall is a back face.
- Default off so it does not clutter Body/Toolpath views.
- Authoring overlay: skipped by collision, cavity, contact shadows, and scene bounds.
