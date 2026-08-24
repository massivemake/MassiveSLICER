# Replit AI Prompt: MassiveSLICER KRL Post-Processing team default

**Paste everything below the line into the Replit AI / agent on lab.massivemake.com.**
MassiveSLICER already implements the client. HTTP 404 = stay local until you ship this.

---

## Who you are building for

- **Product:** MassiveMAKE ERP at **https://lab.massivemake.com**
- **Consumer:** **MassiveSLICER** desktop (Windows shop PCs + macOS)
- **Existing auth:** every `/api/slicer/v1/*` call uses `Authorization: Bearer <msl_…>` (Slicer Access or `POST /login`)
- **Existing libraries:** `GET /presets-bundle`, CRUD `/print-presets`, `/material-presets`, `/mill-tools`
- **Goal:** store **one org-wide KRL Post-Processing default** (Rules + Header + Footer + Code Injector) so every slicer can **Pull from Lab** and know it is the latest factory recipe. **Publish to Lab** writes that default.

## Problem

Today the factory recipe lives only on each PC / git checkout:

`assets/krl_postprocess.json` (repo) and the same object after Done in the KRL Post-Processing dialog.

A new shop PC does not automatically get the team Rules (Robot Mode, Travel Moves, waits, injector, SRC header/footer). MassiveSLICER now calls:

```
GET /api/slicer/v1/krl-postprocess
PUT /api/slicer/v1/krl-postprocess
```

and will also read `krlPostProcess` from `GET /presets-bundle` if you add that object. Those routes **do not exist yet** (the client treats HTTP 404 as "stay local").

This is a **singleton**, not a list. Do **not** model it like print-presets (many named rows). There is exactly **one** team default.

Do **not** break `/print-presets`, `/material-presets`, `/mill-tools`, `/search`, `/pricing`, `/quote`, or existing bearer auth.

## Build this

Same envelope as a single mill-tool / print-preset entry. **Opaque payload.** Do not invent a new KRL schema.

```
GET  /api/slicer/v1/krl-postprocess
PUT  /api/slicer/v1/krl-postprocess
```

Optional (nice, not required):

```
GET  /api/slicer/v1/krl-postprocess/history   // last N versions, newest first
```

Auth: `Authorization: Bearer <token>` on every request.

### GET — 200 when a default exists

```json
{
  "id": "default",
  "updatedAt": "2026-08-21T18:00:00.000Z",
  "updatedBy": "thom@massivemake.com",
  "payload": {
    "SchemaVersion": 1,
    "UpdatedAtUtc": "2026-08-21T18:00:00.000Z",
    "HeaderText": "&ACCESS RVP\nDEF {{PROGRAM_NAME}} ()\n…",
    "FooterText": "$OUT[7]=FALSE\nEND",
    "DefaultHeaderText": "",
    "DefaultFooterText": "",
    "RulesSaved": true,
    "RobotModeEnabled": true,
    "TravelStartStopEnabled": true,
    "ExtruderAirEnabled": false,
    "ApoCvel": 50,
    "SmoothRotation": false,
    "SmoothRotationRadius": 5,
    "SmoothRotationMaxRateDegPerMm": 0,
    "OrientationLookAheadMm": 0,
    "OrientationSigmaMm": 30,
    "ExtrusionStartWaitSec": 0,
    "ExtrusionResumeWaitSec": 0.5,
    "SsPreTravelWaitSec": 0.5,
    "SsResumePrimePercent": 100,
    "ResumeRampEnabled": false,
    "ResumeRampStartSpeed": 0.5,
    "ResumeRampStartRpmPercent": 1,
    "ResumeRampDistanceMm": 609.6,
    "ResumeRampSteps": 10
  }
}
```

`id` may be `"default"` or any stable singleton id. The slicer does not list/delete by id.

### GET — 404

No default published yet. Slicer keeps the local factory file. **Do not 500.**

### PUT — create or replace the singleton

```json
{
  "payload": { "…same object as GET payload…" }
}
```

200 (or 201 on first write) with the same shape as GET. Set `updatedAt` / `updatedBy` from the bearer user. Replace the previous default (keep history only if you add `/history`).

Unknown fields inside `payload` **must round-trip**. This is desktop JSON (`KrlPostProcessSettings`). PascalCase as shown; case-insensitive read is fine.

### Also add to GET /presets-bundle

```json
{
  "version": "…",
  "printPresets": [ ],
  "materialPresets": [ ],
  "millTools": [ ],
  "krlPostProcess": { "id": "default", "updatedAt": "…", "updatedBy": "…", "payload": { } }
}
```

If no default exists, omit `krlPostProcess` or set it `null`. Do not use an empty array.

## Sync rules (desktop already does this)

| Action | Desktop |
|---|---|
| Connect to Lab | `GET /krl-postprocess` (fallback: bundle). If 200, **Lab wins** — apply + write local factory. If 404, leave local. |
| **Publish to Lab** | `PUT` current recipe. Explicit — never auto-overwrite Lab on connect. |
| **Pull from Lab** | Same as connect pull, on demand. |
| Import / Export | Local `.json` only. Envelope `{ kind: "MassiveSLICER.KrlPostProcess", schemaVersion: 1, updatedAt, payload }` **or** a bare payload. Lab only stores the payload. |

## CORS / verbs

- Allow `GET` and `PUT` (and `OPTIONS`).
- PATCH is **not** used. DELETE is **not** used for v1.

## Verify when done

```bash
export BASE="https://lab.massivemake.com/api/slicer/v1"
export TOKEN="msl_…"

# 404 or 200
curl -sS -D- -H "Authorization: Bearer $TOKEN" "$BASE/krl-postprocess"

# Publish
curl -sS -D- -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"payload":{"SchemaVersion":1,"RulesSaved":true,"RobotModeEnabled":true,"TravelStartStopEnabled":false,"ApoCvel":50,"HeaderText":"DEF {{PROGRAM_NAME}}()","FooterText":"END"}}' \
  "$BASE/krl-postprocess"

# Bundle includes the object
curl -sS -H "Authorization: Bearer $TOKEN" "$BASE/presets-bundle" | head

# No bearer
curl -sS -D- "$BASE/krl-postprocess"   # expect 401
```

Reply with: live paths, example 200 JSON (redact tokens), and whether `presets-bundle` now includes `krlPostProcess`.
