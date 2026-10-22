# Multi-Planar switch by layer

Even layers keep the guide tilt. Odd layers lean the opposite way by the same angle.

## Sub-features

- GEOMETRY → Multi-Planar → **Switch by layer** (default off)
- Off: tilt climbs through the guide planes
- On: layer 0, 2, 4… keep the interpolated angle; layer 1, 3, 5… flip it
- `addset MultiPlanarSwitchByLayer true|false`

## How to get to it

Slice method Multi-Planar. Right GEOMETRY. The switch sits under Tilt axis, above the guide-plane rows.

## Driving it

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py console --cmd "addset MultiPlanarSwitchByLayer true"
```

Pass: a re-slice shows consecutive layers leaning opposite ways. Off returns to a smooth climb. Do not treat this as a toolhead A/B/C flip or a tilt-axis change.
