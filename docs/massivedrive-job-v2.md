# massivedrive.job/v2 — Slicer ↔ Drive job directory

Large LFAM sends (curtain-scale, ~1.6M segments) must not POST a 1 GB JSON
array. Slicer writes executor-native files to a **shared disk** the Drive host
can see, then POSTs a **tiny pointer**.

MassiveDRIVE’s own PR may still be in flight. This file is the Slicer-side
contract. Keep the binary layout stable; if Drive documents a different v2
layout, update both sides together.

## Job directory

```
{jobsRoot}/{job_id}/
  manifest.json     # massivedrive.job/v2 — no segments array
  segments.bin      # fixed-stride little-endian records
  preview.json      # ~10–20k xyz for Cell 3D
  summary.json      # counts, bounds, duration, sha256
```

`job_id` is a 12-char hex id (same as v1). Relative root in the pointer is
that folder name.

## Share path (LFAM 3)

Cell JSON (`massiveDriveJobsRoot`), overridable per machine in prefs
(`MassiveDriveJobsRoot`):

| Machine | Typical path |
|---|---|
| Drive host | `/var/jobs` (or whatever Drive mounts) |
| Shop Windows PC | `\\192.168.0.201\MassiveDRIVE\var\jobs` or a mapped letter |
| macOS mount | `/Volumes/MassiveDRIVE/var/jobs` |

The Samba share currently allows user **`massive`**. If write is denied, Slicer
copies the job to local staging (`%LOCALAPPDATA%\MassiveSlicer\drive-jobs` or
`MassiveDriveJobsStaging`) and **surfaces an error** — Drive cannot see the
staging folder. Do not Send a curtain job until the share is writable.

## Pointer POST

`POST /api/jobs/package/pointer`

```json
{
  "format": "massivedrive.job/v2",
  "job_id": "a1b2c3d4e5f6",
  "name": "Curtain",
  "root": "a1b2c3d4e5f6",
  "sha256": "<hex sha256 of segments.bin>"
}
```

No `segments` array. HTTP should finish in milliseconds once the files are on
the share. File write time dominates.

## Legacy v1 JSON

`POST /api/jobs/package` with `massivedrive.job/v1` (full segment array) is
kept for **small jobs** (`< 8000` segments and estimated JSON `< 4 MB`) or when
prefs `MassiveDriveForceLegacyJson` is true. Large jobs never put the segment
array on the wire.

## `segments.bin`

Little-endian. Header is 32 bytes; each record is **80 bytes**.

### Header

| Offset | Type | Field |
|---|---|---|
| 0 | 8 bytes ASCII | `MDSEG2\0\0` |
| 8 | u32 | version = `2` |
| 12 | u32 | record stride = `80` |
| 16 | u32 | record count |
| 20 | u32 | flags (bit 0 = records include `rpm_pct`) |
| 24 | 8 bytes | reserved |

### Record (stride 80)

| Offset | Type | Field |
|---|---|---|
| 0 | i32 | `i` (index) |
| 4 | u8 | kind: `1=print`, `2=travel`, `3=mill` |
| 5 | u8 | flags (see below) |
| 6 | i16 | layer |
| 8 | 6 × f32 | from `x y z a b c` (print-bed BASE, mm / deg) |
| 32 | 6 × f32 | to `x y z a b c` |
| 56 | f32 | `speed_mm_s` |
| 60 | f32 | `flow_scale` |
| 64 | u16 | `rpm_pct` (`0` = none) |
| 66 | u16 | reserved |
| 68 | 3 × i32 | reserved |

Flags: `reverse=1`, `layer_change=2`, `wipe=4`, `resume_ramp=8`,
`pre_travel_start=16`, `post_travel_end=32`.

## `manifest.json`

Same envelope as v1 (`cell_id`, `job_id`, `name`, `source`, `units`,
`frames.tool`, `frames.base`, `defaults`, `meta`) with
`"format": "massivedrive.job/v2"`. **No `segments` array.** Adds:

```json
{
  "files": {
    "segments": "segments.bin",
    "preview": "preview.json",
    "summary": "summary.json"
  },
  "segment_file": {
    "name": "segments.bin",
    "count": 1600000,
    "stride": 80,
    "encoding": "le-fixed",
    "magic": "MDSEG2"
  }
}
```

`frames.tool` / `frames.base` are the live ROBOT CELL TOOL # / BASE #
(including LFAM 3 heated **BASE #6**). Do not hardcode BASE 1.

## `preview.json`

```json
{
  "format": "massivedrive.preview/v1",
  "count": 16000,
  "xyz": [x0, y0, z0, x1, y1, z1]
}
```

Downsampled while writing `segments.bin` (target 16k points, clamp 10–20k).

## Code

| Piece | File |
|---|---|
| Typed segments + v1/v2 envelope | `MassiveDriveJobExporter.cs` |
| Binary records | `MassiveDriveSegmentBinary.cs` |
| Directory writer | `MassiveDriveJobV2Writer.cs` |
| Pointer + share policy | `MassiveDriveJobV2.cs` |
| HTTP pointer | `MassiveDriveClient.UploadPackagePointerAsync` |
| Send UX | `ViewportView.SendToMassiveDriveAsync` |
