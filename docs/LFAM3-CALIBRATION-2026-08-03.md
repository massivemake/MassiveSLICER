# LFAM 3 cell calibration — 2026-08-03

Session notes for branch `feature/spsm`. Calibration is stored in committed cell JSON
(`assets/cells/LFAM3/lfam3.json` and the mirrored source copies) so a fresh clone loads
these values without re-teaching.

## How it was measured

Both routines use **MassiveDRIVE Movements as motion master** (brain `192.168.0.233:8080`):

| Routine | Drive movement | Notes on waypoints | SLICER action |
|---------|----------------|--------------------|---------------|
| Scan-tool hand-eye | **Scanner Calibration** | `scan` | Zivid hand-eye pose → tool **#6** TCP |
| Rotary bed centre | **Bed Calibration** | `bed` + taught **E1** | Board sample + surface cloud → fit centre |

Drive advances only after **capture-ack** (not a fixed timer). Moves wait for **TCP + E1**
and a short stability gate so captures do not fire while the bed is still turning.

Pendant program: `LFAM3_RSI_BulkPTP` (AUT, drives on, path idle).

## Saved results (in `lfam3.json`)

### Scanner tool #6 (hand-eye)

| Field | Value |
|-------|------:|
| tcpX / sensorOriginX | −55.30 mm |
| tcpY / sensorOriginY | −103.64 mm |
| tcpZ / sensorOriginZ | 260.18 mm |
| tcpA / sensorOriginA | 0.084° |
| tcpB / sensorOriginB | 2.457° |
| tcpC / sensorOriginC | −1.211° |

Tool **#5** (`ZividScanner`) remains the uncalibrated working frame used during the sweep.
Hand-eye **result** lives on tool **#6** (`Scanner`).

### Rotary bed

| Field | Value |
|-------|------:|
| basePos | (2135.45, −52.54, −654.87) mm |
| baseAbc | (−6.033, 0, −90)° |
| e1Sign | +1 (CCW in cell convention at save time) |
| orientationOffsetDeg | −0.99° (unchanged this session) |

## For a new user

1. Clone / pull this branch; load **LFAM 3**.
2. Point `massiveDriveUrl` at the shop brain if the IP differs.
3. Teach / keep **Scanner Calibration** and **Bed Calibration** movements on MassiveDRIVE
   (poses + E1 live on the brain, not in this repo).
4. Re-run **Auto-Calibrate Scan Tool** / **Auto-Calibrate Bed** only if hardware moved;
   otherwise the committed TCP and bed centre are the defaults.

## Related code

- Drive sequence status: `GET /api/sequences/run/status`, `POST …/capture-ack`
- SLICER: `MainWindowViewModel` Drive-master scan-cal / bed-cal; `MassiveDriveWaypointNotes`
- Cell write: `CellLoader.TrySaveToolTcp` / bed apply → `lfam3.json`
