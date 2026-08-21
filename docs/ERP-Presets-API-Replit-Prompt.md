# Replit AI Prompt: MassiveSLICER Shared Presets API

**Paste everything below the line into the Replit AI / agent.**  
This is a build request for `lab.massivemake.com` (MassiveMAKE ERP). MassiveSLICER (desktop) will implement the client later — you only need the **server/API** side now.

---

## Who you are building for

- **Product:** MassiveMAKE ERP hosted on Replit at **https://lab.massivemake.com**
- **Consumer app:** **MassiveSLICER** (Windows/macOS desktop slicer)
- **Existing integration:** MassiveSLICER already calls **`/api/slicer/v1/*`** with bearer tokens (`Authorization: Bearer msl_…` from ERP Settings → Slicer Access)
- **Goal:** Store **print presets** and **material presets** on the ERP so any MassiveSLICER instance can pull the shared library on open/connect and push when the user saves — instead of only keeping them in local AppData on each PC

---

## Problem we are solving

Today MassiveSLICER saves two libraries **only on the local machine**:

| Library | Local path (Windows) | Purpose |
|--------|----------------------|---------|
| Print presets | `%AppData%/MassiveSlicer/presets.json` | Named snapshots of Additive slicing settings |
| Material presets | `%AppData%/MassiveSlicer/materials.json` | Material profiles (temps, flow rates, density, calibration) |

When someone opens MassiveSLICER on a new PC (or a teammate’s machine), **they do not see presets saved elsewhere**. We want the ERP (`lab.massivemake.com`) to be the **source of truth** for the team library.

MassiveSLICER already uses this ERP for:

- Project/lead search
- Elements
- Slice registration (metadata + UNAS paths)
- Pricing + quotes

**There are no preset endpoints yet.** Build them under the same Slicer API surface and auth.

---

## What already exists on the ERP (do not break)

Base: **`/api/slicer/v1`**

| Method | Path | Status |
|--------|------|--------|
| GET | `/search?q=` | Live — empty `q` often returns 400 (“q must be at least 2 characters”); slicer treats that as reachable + authorized |
| GET | `/projects/{id}/elements` | Live |
| POST | `/projects/{id}/elements` | Live (leads may 400 if converted) |
| POST | `/elements/{id}/slices` | Live (metadata only; no binary upload) |
| GET | `/pricing` | Live |
| POST | `/quote` | Live |

Auth on every request:

```http
Authorization: Bearer msl_<token>
```

Invalid/revoked token → **401/403**. Other failures → clear JSON or message body (slicer surfaces human-readable errors).

Default client base URL:

```text
https://lab.massivemake.com/api/slicer/v1
```

Implement new routes under this same prefix and auth middleware.

---

## What we need you to build

### Scope (ERP / Replit only)

1. **Database / storage** for print presets and material presets (org-wide shared library is fine for v1).
2. **REST CRUD + list** endpoints under `/api/slicer/v1/…`.
3. **Stable server IDs** + `updatedAt` for sync/conflict.
4. **Opaque JSON payloads** matching MassiveSLICER’s existing shapes (below) — do not invent a new settings schema.
5. **Docs** (short) of the endpoints + example request/response.
6. Optional: seed with empty lists; migration path if none.

### Out of scope (desktop will do later)

- MassiveSLICER UI / C# client changes
- Uploading `.mass` files or tool meshes
- Replacing ERP **pricing materials** catalog (`GET /pricing`) — that stays separate from calibrated material presets

---

## Two libraries (keep separate)

### A) Print presets

Named snapshots of **Additive** panel settings. Partial records are normal: fields the user did not save are **null / omitted**.

**Core identity fields (always):**

| Field | Type | Notes |
|-------|------|--------|
| Name | string | Display name |
| Folder | string | e.g. `"Uncategorized"`, `"Production"`, `"Imported"` |
| CreatedUtc | ISO datetime | |
| LastPrintedUtc | ISO datetime or null | Optional |
| IsFavorite | boolean | |
| Material | string | **Name only** of material preset (soft link), e.g. `"ASA GF - Black"` |

**Nullable settings groups** (any subset may be present). Desktop field names (PascalCase as in JSON):

- **Geometry & layers:** `BeadWidth`, `LayerHeight`, `TiltAngle`, `TiltAngleX`, `MultiPlanarAxisX`, `MultiPlanarPlanes` (array of `{ HeightPct, AngleDeg }`)
- **Slicing mode & method:** `Method`, `SeamMode`, `SlicingMode`, `OrientationFollowPercent`, `OrientationMaxTiltDeg`, `FirstLayerZeroTilt`, `LayerLeanPercent`, `LayerLeanMaxTiltDeg`, `CurvedBoundarySourceDisplay`, `CurvedAutoDetectBandMm`, `CurvedEnableRegionSplit`
- **Live effector:** `EffectorEnabled`, `EffectorMode`, `EffectorRange`, `EffectorStrength`
- **Pattern & texture:** `PatternType`, `PatternMapping`, `PatternWavelengthMm`, `PatternAmplitude`, `PatternFrequency`, `PatternTwist`, `PatternOffset`, `PatternFadeIn`, `PatternFadeOut`
- **X-Bracing wall:** `XBracingEnabled`, `XBracingProjectionType`, `XBracingShowHelper`, `XBracingPlaneTiltY`, `XBracingPlaneTiltX`, `XBracingCylinderDiameterMm`, `XBracingCylinderFlipDirection`, `XBracingDepthMm`, `XBracingDepthBottomMm`, `XBracingDepthEaseBottom`, `XBracingDepthEaseTop`, `XBracingSpanMm`, `XBracingAngleDeg`, `XBracingExtendEdges`  
  *(Do not require world-space cylinder X/Y — those are per-model and intentionally not portable.)*
- **Wave effect:** `WaveEffect`, `WaveAmplitude`, `WaveFrequencyMode`, `WaveWavelength`, `WaveCycles`, `WaveShape`, `WaveStagger`, `WavePhaseMethodIndex`, `WaveGradient`, `WaveAmplitudeBottom`, `WaveAmplitudeTop`, `WaveWavelengthBottom`, `WaveWavelengthTop`, `WaveGradientCenter`, `WaveGradientCurve`
- **Infill:** `InfillPattern`, `InfillSpacingMm`, `InfillAngleDeg`, `LightningOverhangDeg`, `LightningBranchSpacingMm`, `LightningTipLoopRadiusMm`, `LightningAffectInterior`, `LightningAffectExterior`, `LightningTargetSupportSelections`, `LightningButtressBarMm`
- **Overhang & orientation:** `OverhangOrientation`, `MaxOverhangTiltDeg`, `ZigZagAllowSameLayerTravel`, `DisableContourOffset`
- **Toolhead orientation:** `ToolheadA`, `ToolheadB`, `ToolheadC`
- **Motion & KUKA frame:** `PrintSpeed`, `TravelSpeed`, `ApoCvel`, `E1MotionEnabled`, `E1YPlusMm`, `E1YMinusMm`, `SmoothRotation`, `SmoothRotationRadius`, `SmoothRotationMaxRateDegPerMm`, `OrientationLookAheadMm`, `OrientationSigmaMm`
- **Temperatures:** `Temperature1`, `Temperature2`, `Temperature3`
- **KRL export tuning:** `TemperatureOffset`, `ExtrusionSpeedOffset`, `DigitalStartStopEnabled`, `ExtrusionStartWaitSec`, `ExtrusionResumeWaitSec`
- **Movement:** `ZHopMm`, `WipeModeDisplay`, `WipeLengthMm`, `WipeRampMm`, `WipeSpeed`, `WipeSkipShortTravels`, `ResumeRampEnabled`, `ResumeRampStartSpeed`, `ResumeRampStartRpmPercent`, `ResumeRampDistanceMm`, `ResumeRampSteps`
- **Adaptive layer speed:** `LayerSpeedAdaptEnabled`, `LayerSpeedBasisDisplay`, `LayerSpeedMinMmS`, `LayerSpeedMaxMmS`
- **KRL post-process:** `KrlHeaderText`, `KrlFooterText`
- **Adaptive layer height:** `AdaptiveLayerHeight`, `MinLayerHeight`, `AdaptiveQuality`
- **Stock from Maps:** `UseDisplacedStock`, `StockAllowanceMm`
- **Brim:** `BrimEnabled`, `BrimLoops`

**Storage rule:** Treat the desktop payload as **opaque JSON**. You may store it as a JSON column. Unknown future keys should round-trip. Do not require every field.

### B) Material presets

Calibrated material library (different from pricing catalog).

| Field | Type | Notes |
|-------|------|--------|
| Name | string | e.g. `"ASA GF - Black"` |
| MaterialType | string | e.g. `"ASA"`, `"PETG"` |
| Color | string | e.g. `"Black"` |
| Temperature1 / 2 / 3 | number | °C zone setpoints |
| FlowRate | number | HV extruder rev/cm³ |
| FlowRateHf | number | HF extruder; `0` means “use FlowRate” |
| MaterialDensity | number | g/cm³ |
| CostPerLb | number | Local estimate; ERP pricing may override for quotes |
| GlassTransitionC | number | `0` = auto from MaterialType |
| ThermalBondMarginC | number | default 10 |
| ThermalSagMarginC | number | default 45 |
| ThermalAmbientC | number | default 30 |
| CalibratedOn | string | `yyyy-MM-dd` or empty |
| CalibrationNote | string | |
| CalibMotorPercent, CalibTimeSec, CalibWeightG | number | HV calibration inputs |
| CalibIsHf | boolean | UI hint |
| CalibMotorPercentHf, CalibTimeSecHf, CalibWeightGHf | number | HF calibration |
| CalibratedOnHf, CalibrationNoteHf | string | HF provenance |

---

## API contract to implement

Use the same auth as other `/api/slicer/v1` routes.

### Server-owned metadata (wrap desktop payload)

```json
{
  "id": "01JABCDEFG...",
  "updatedAt": "2026-08-16T18:00:00.000Z",
  "updatedBy": "optional label from token/user",
  "payload": { }
}
```

- `id`: stable unique string (UUID or ULID). **Do not use Name alone as primary key** (names collide).
- `updatedAt`: ISO-8601 UTC; bump on every write.
- `payload`: exact MassiveSLICER record (`PrintPresetRecord` or `MaterialPreset` shape).

Optional uniqueness: unique index on `payload.Name` (or Name+Folder) org-wide — if you enforce it, return **409** with a clear message on conflict.

### Print presets

```
GET    /api/slicer/v1/print-presets
GET    /api/slicer/v1/print-presets/:id
POST   /api/slicer/v1/print-presets
PUT    /api/slicer/v1/print-presets/:id
DELETE /api/slicer/v1/print-presets/:id
```

### Material presets

```
GET    /api/slicer/v1/material-presets
GET    /api/slicer/v1/material-presets/:id
POST   /api/slicer/v1/material-presets
PUT    /api/slicer/v1/material-presets/:id
DELETE /api/slicer/v1/material-presets/:id
```

### Bundle (recommended for desktop startup sync)

```
GET    /api/slicer/v1/presets-bundle
```

Response:

```json
{
  "version": "2026-08-16T18:00:00.000Z",
  "printPresets": [
    {
      "id": "…",
      "updatedAt": "…",
      "updatedBy": null,
      "payload": {
        "Name": "Master Defaults",
        "Folder": "Uncategorized",
        "CreatedUtc": "2026-07-31T20:07:34.6575487Z",
        "IsFavorite": false,
        "Material": "",
        "BeadWidth": 6,
        "LayerHeight": 3,
        "Method": "Planar"
      }
    }
  ],
  "materialPresets": [
    {
      "id": "…",
      "updatedAt": "…",
      "payload": {
        "Name": "ASA GF - Black",
        "MaterialType": "ASA",
        "Color": "Black",
        "Temperature1": 250,
        "Temperature2": 250,
        "Temperature3": 250,
        "FlowRate": 0.4115,
        "FlowRateHf": 0.6019,
        "MaterialDensity": 1.17,
        "CostPerLb": 5,
        "GlassTransitionC": 0,
        "ThermalBondMarginC": 10,
        "ThermalSagMarginC": 45,
        "ThermalAmbientC": 30,
        "CalibratedOn": "",
        "CalibrationNote": "",
        "CalibMotorPercent": 50,
        "CalibTimeSec": 60,
        "CalibWeightG": 0,
        "CalibIsHf": false,
        "CalibMotorPercentHf": 50,
        "CalibTimeSecHf": 60,
        "CalibWeightGHf": 0,
        "CalibratedOnHf": "",
        "CalibrationNoteHf": ""
      }
    }
  ]
}
```

`version` can be max(`updatedAt`) or a content hash — desktop will use it to skip no-op syncs later.

### POST create (print example)

Request body — either wrap or bare payload (accept **both** for convenience):

**Option A (preferred):**

```json
{
  "payload": {
    "Name": "HHN Nasty Wall",
    "Folder": "Production",
    "CreatedUtc": "2026-08-16T12:00:00.000Z",
    "IsFavorite": false,
    "Material": "ASA GF - Black",
    "BeadWidth": 6.5,
    "LayerHeight": 3.0,
    "PrintSpeed": 100
  }
}
```

**Option B (bare desktop export):** same object as `payload` alone at top level.

Response **201**:

```json
{
  "id": "01J…",
  "updatedAt": "2026-08-16T12:00:01.000Z",
  "updatedBy": null,
  "payload": { "…full stored payload…" }
}
```

### PUT update

- Full replace of `payload` (or merge only if you document it — **prefer full replace**).
- Body: same as POST.
- **404** if id missing.
- Response **200** with updated record.

### DELETE

- **204** or **200** `{ "ok": true }`
- **404** if missing

### GET list

```json
{
  "version": "…",
  "items": [ { "id", "updatedAt", "updatedBy", "payload" } ]
}
```

Optional query: `?since=ISO` to return only records with `updatedAt > since` (nice for later incremental sync; not required for v1).

### Errors

Consistent with existing Slicer API style:

```json
{ "error": "human readable message", "message": "human readable message" }
```

| Status | When |
|--------|------|
| 401 / 403 | Bad or revoked token |
| 400 | Missing Name, invalid JSON |
| 404 | Unknown id |
| 409 | Name conflict (if uniqueness enforced) |
| 500 | Server error |

---

## Auth & multi-tenancy

- Reuse existing **Slicer Access** tokens (`msl_…`).
- v1: **one shared org library** for all tokens on this ERP (simplest; matches “team presets”).
- Later (optional): per-user libraries or scopes — not required now.
- Do **not** require the desktop to send secrets other than the existing bearer token.

---

## Conflict / sync rules (document in code comments)

1. Server is source of truth after first successful pull.
2. Last-write-wins via `updatedAt` is OK for v1.
3. Desktop will keep local cache offline; it will PUT after local save when online.
4. Do not delete local-only names on the server unless DELETE is called for that `id`.

---

## Example material payload (real shape from desktop)

```json
{
  "Name": "PETG - Clear",
  "MaterialType": "PETG",
  "Color": "Clear",
  "Temperature1": 240,
  "Temperature2": 240,
  "Temperature3": 240,
  "FlowRate": 0.463,
  "FlowRateHf": 0,
  "MaterialDensity": 1,
  "CostPerLb": 5,
  "GlassTransitionC": 0,
  "ThermalBondMarginC": 10,
  "ThermalSagMarginC": 45,
  "ThermalAmbientC": 30,
  "CalibratedOn": "",
  "CalibrationNote": "",
  "CalibMotorPercent": 50,
  "CalibTimeSec": 60,
  "CalibWeightG": 0,
  "CalibIsHf": false,
  "CalibMotorPercentHf": 50,
  "CalibTimeSecHf": 60,
  "CalibWeightGHf": 0,
  "CalibratedOnHf": "",
  "CalibrationNoteHf": ""
}
```

## Example print payload (partial — nulls omitted is normal)

```json
{
  "Name": "Master Defaults",
  "Folder": "Uncategorized",
  "CreatedUtc": "2026-07-31T20:07:34.6575487Z",
  "IsFavorite": false,
  "Material": "",
  "BeadWidth": 6,
  "LayerHeight": 3,
  "TiltAngle": 0,
  "TiltAngleX": 0,
  "MultiPlanarAxisX": false,
  "MultiPlanarPlanes": [
    { "HeightPct": 0, "AngleDeg": 0 },
    { "HeightPct": 50, "AngleDeg": 15 },
    { "HeightPct": 100, "AngleDeg": 30 }
  ],
  "Method": "Planar",
  "SeamMode": "Normal",
  "SlicingMode": "Normal",
  "PrintSpeed": 100,
  "TravelSpeed": 250
}
```

---

## Acceptance checklist (please verify before done)

- [ ] Bearer auth required on all new routes (same as `/pricing`, `/search`)
- [ ] `GET /print-presets` and `GET /material-presets` return empty arrays when none exist (not 404)
- [ ] `GET /presets-bundle` returns both lists + `version`
- [ ] `POST` creates record, returns `id` + `updatedAt` + stored `payload`
- [ ] `PUT :id` updates payload and bumps `updatedAt`
- [ ] `DELETE :id` removes record
- [ ] Unknown JSON fields inside `payload` are preserved (do not strip)
- [ ] Null / omitted fields allowed (partial print presets)
- [ ] Does not break existing `/search`, `/pricing`, `/quote`, `/elements`, `/slices`
- [ ] Short README or comment in repo documenting the new routes
- [ ] Smoke-test with curl using a real `msl_` token if available

### Curl smoke tests (for you / us after deploy)

```bash
# Replace TOKEN
export TOKEN="msl_..."
export BASE="https://lab.massivemake.com/api/slicer/v1"

curl -sS -H "Authorization: Bearer $TOKEN" "$BASE/presets-bundle" | head

curl -sS -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"payload":{"Name":"API Test Material","MaterialType":"ASA","Color":"Black","Temperature1":250,"Temperature2":250,"Temperature3":250,"FlowRate":0.4,"FlowRateHf":0,"MaterialDensity":1.1,"CostPerLb":5}}' \
  "$BASE/material-presets"

curl -sS -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"payload":{"Name":"API Test Print","Folder":"Uncategorized","CreatedUtc":"2026-08-16T00:00:00Z","IsFavorite":false,"Material":"API Test Material","BeadWidth":6.5,"LayerHeight":3}}' \
  "$BASE/print-presets"

curl -sS -H "Authorization: Bearer $TOKEN" "$BASE/print-presets"
curl -sS -H "Authorization: Bearer $TOKEN" "$BASE/material-presets"
```

---

## How MassiveSLICER will use this later (for your design context only)

1. User connects ERP in Preferences (URL + token already exist).
2. On connect / app start: `GET /presets-bundle` → merge into local `presets.json` / `materials.json`.
3. User hits Save Preset / Save Material in desktop → write local file **and** `POST` or `PUT` server.
4. User deletes → `DELETE`.
5. Offline → local only; push when online (future).

You do **not** need to implement the desktop side. Just make the API correct and stable.

---

## Success definition

A second MassiveSLICER install (or wiped AppData) can, after connecting with a valid slicer token, **download the same print and material presets** that another machine uploaded — without copying `%AppData%` folders by hand.

---

## Implementation notes for Replit agent

- Follow existing patterns for `/api/slicer/v1` routes, auth middleware, and error formatting in this codebase.
- Prefer a simple table(s) or collection: `print_presets`, `material_presets` with columns `id`, `updated_at`, `updated_by`, `payload` (JSON).
- Case-insensitive property names on input are fine; store as sent when possible (desktop uses PascalCase).
- Do not put binary files in these endpoints.
- Keep pricing materials (`GET /pricing`) separate from `material-presets` (calibrated slicer library).

**Please implement this end-to-end, migrate the DB if needed, deploy/restart the app, and reply with the final route list + example responses from a live curl against lab.massivemake.com.**
