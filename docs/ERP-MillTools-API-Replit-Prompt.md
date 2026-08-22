# Replit AI Prompt: MassiveSLICER cutting-tool (mill bit) library

**Paste everything below the line into the Replit AI / agent.**
This is a build request for `lab.massivemake.com` (MassiveMAKE ERP). MassiveSLICER already implements the client.

---

## Who you are building for

- **Product:** MassiveMAKE ERP at **https://lab.massivemake.com**
- **Consumer:** **MassiveSLICER** desktop (Windows shop PCs + macOS)
- **Existing auth:** every `/api/slicer/v1/*` call uses `Authorization: Bearer <token>` (Slicer Access `msl_…` or the pending login route)
- **Existing libraries:** print presets (`/print-presets`) and material presets (`/material-presets`) plus optional `GET /presets-bundle`
- **Goal:** store the **Cutting Tool Library** (mill bits / end mills / drills) the same way, so any slicer can pull the team library on connect and push when the user saves a bit

## Problem

Today mill bits live only on each PC:

`%AppData%/MassiveSlicer/mill_tools.json` (schema v3)

A new shop PC does not see tools saved on another machine. MassiveSLICER now calls:

```
GET/POST /api/slicer/v1/mill-tools
PUT/DELETE /api/slicer/v1/mill-tools/:id
```

and will also read `millTools` from `GET /presets-bundle` if you add that array. Those routes **do not exist yet** (the client treats HTTP 404 as "stay local").

Do **not** break `/print-presets`, `/material-presets`, `/search`, `/pricing`, `/quote`, or existing bearer auth.

## Build this collection

Same envelope as print/material presets. **Opaque payload.** Do not invent a new mill schema.

```
GET    /api/slicer/v1/mill-tools
GET    /api/slicer/v1/mill-tools/:id
POST   /api/slicer/v1/mill-tools
PUT    /api/slicer/v1/mill-tools/:id
DELETE /api/slicer/v1/mill-tools/:id
```

Auth: `Authorization: Bearer <token>` on every request.

### POST / PUT body

```json
{
  "payload": {
    "Id": "lfam3-ap90-flat-3in",
    "Name": "Flat end D76.2 (AP90 FLAT 3in End Mill)",
    "Identifier": "AP90 FLAT 3in End Mill",
    "ToolNumber": 10,
    "Type": "FlatEndMill",
    "DiameterMm": 76.2,
    "ShaftDiameterMm": 76.2,
    "CornerRadiusMm": 0,
    "TotalLengthMm": 0,
    "FluteLengthMm": 6.25,
    "ShoulderLengthMm": 0,
    "LengthBelowHolderMm": 0,
    "FluteCount": 1,
    "MaxDepthMm": 0,
    "IsDefaultSpindleBit": true,
    "ShowSpindleCylinder": true,
    "CylinderLengthMm": 0,
    "CylinderFlip": false,
    "LastModifiedUtc": "2026-08-16T20:00:00Z",
    "HolderSegments": [
      { "HeightMm": 0, "TopDiameterMm": 0, "BottomDiameterMm": 0 }
    ],
    "CuttingPresets": [
      {
        "Name": "Default",
        "SpindleRpm": 2088,
        "SurfaceSpeedMPerMin": 499.845,
        "CuttingFeedMmS": 10.44,
        "FeedPerToothMm": 0,
        "PlungeFeedMmMin": 1000,
        "StepoverMm": 3,
        "StepdownMm": 2,
        "FinishAllowanceMm": 0.3,
        "RapidZMm": 50,
        "SpindleDirection": "Clockwise"
      }
    ]
  }
}
```

`payload` is a `MillBitTool`. Extra fields must round-trip. Do **not** strip `Id` (desktop tool guid). Do **not** require `ErpId` in the payload (server owns the row id).

`Type` enum: `BallEndMill` | `FlatEndMill` | `BullNose` | `Drill` | `Other`.
`SpindleDirection`: `Clockwise` | `CounterClockwise`.

### List / get / create / update response (one row)

```json
{
  "id": "mt_…",
  "updatedAt": "2026-08-16T20:00:00.000Z",
  "updatedBy": null,
  "payload": { }
}
```

List may wrap as `{ "items": [ … ] }` or `{ "millTools": [ … ] }` or a bare array. Empty library = `[]` (200), not 404.

### Also add to GET /presets-bundle (if that route exists)

```json
{
  "version": "2026-08-16T20:00:00.000Z",
  "printPresets": [ ],
  "materialPresets": [ ],
  "millTools": [ { "id": "mt_…", "updatedAt": "…", "payload": { } } ]
}
```

If `/presets-bundle` is not shipped yet, list-only is enough. The slicer falls back to `GET /mill-tools`.

### Errors

| Status | When |
|--------|------|
| 400 | missing payload |
| 401 / 403 | bad or missing bearer |
| 404 | unknown `:id` on GET/PUT/DELETE. **Not** for an empty list. |
| 409 | optional unique-name conflict |

JSON error body with `error` or `message`.

### Do not

- Do not store cutter meshes / GLBs (geometry numbers only)
- Do not merge this with KUKA `TOOL_DATA` / cell tools (T1…T12)
- Do not merge this with `GET /pricing` materials
- Do not change existing print/material preset routes

## Verify after deploy

```bash
export BASE="https://lab.massivemake.com/api/slicer/v1"
export TOKEN="msl_…"   # a real Slicer Access token

# empty list must be 200
curl -sS -o /tmp/mill.json -w "%{http_code}\n" \
  -H "Authorization: Bearer $TOKEN" "$BASE/mill-tools"

# create
curl -sS -o /tmp/mill-create.json -w "%{http_code}\n" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"payload":{"Id":"test-bit","Name":"Test 6mm ball","Type":"BallEndMill","DiameterMm":6,"FluteCount":2,"CuttingPresets":[{"Name":"Default","SpindleRpm":12000,"CuttingFeedMmS":50}]}}' \
  "$BASE/mill-tools"

# bundle (optional)
curl -sS -o /tmp/bundle.json -w "%{http_code}\n" \
  -H "Authorization: Bearer $TOKEN" "$BASE/presets-bundle"
```

Reply with: live paths, example 200 JSON (redact tokens), and whether `presets-bundle` now includes `millTools`.
