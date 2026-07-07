# MassiveSLICER ↔ ERP Slicer API

What the slicer client (MassiveSLICER, `src/MassiveSlicer.App/Erp/`) calls, what it
sends, and what it still needs the ERP to ship. Verified against
`https://lab.massivemake.com/api/slicer/v1` on 2026-07-07.

Auth: `Authorization: Bearer msl_…` on every request (tokens from ERP Settings →
Slicer Access). The slicer maps 401/403 to "token invalid or revoked" and any other
non-2xx to a user-visible failure. It stores the token in local prefs only — never
in shared `.mass` files.

## Working today (live on production)

### GET /search?q={query}
- Health check: the slicer pings with an empty `q` and treats the HTTP 400
  ("q must be at least 2 characters") as *reachable + authorized*.
- Response envelope: `{ "query", "projects": [...], "leads": [...] }` — both arrays
  are read and concatenated.
- Hit fields (first match wins, case-insensitive): `type|kind`,
  `id|projectId|leadId` (string or number), `number|no|projectNumber|leadNumber`,
  `title|name`, `client|clientName|customer`, optional embedded `elements`.

### GET /projects/{id}/elements
- Response envelope: `{ "project": {...}, "elements": [...] }` (bare array also fine).
- Element fields: `id|elementId`, `name|title|elementName`,
  `elementNumber|element|number|no`, rev count from
  `revCount|revisionCount|revisions|currentRevCount|sliceCount`.

## Needed next (slicer UI already ships these calls; production returns 404)

### 1. POST /projects/{id}/elements — and — POST /leads/{id}/elements
Create an element from the slicer (the dock offers this when a project/lead has no
element, prefilled with the workspace name).

Request:
```json
{ "name": "2026_0706 - Curtain Wall For Print" }
```
(`description` is included when non-null, omitted otherwise.)

Response — 201, element wrapped or bare (both accepted):
```json
{ "element": { "id": 41, "elementNumber": 1, "name": "2026_0706 - Curtain Wall For Print" } }
```

**Open question for the ERP:** can leads own elements directly? If not, return a
400 with a human-readable `error` and the slicer will surface it verbatim — or
decide on lead→project conversion semantics.

### 2. POST /elements/{id}/slices
Register a slice revision. **Metadata only — no bytes are uploaded.** Heavy files
live on the UNAS share; the ERP resolves them via its UNAS API.

Request (real example from the verified flow):
```json
{
  "stats": {
    "printTime": "19h 26m 24s",
    "weight": "146.925 lbs",
    "material": "ASA - Black",
    "layerHeightMm": 3.0,
    "beadWidthMm": 6.5
  },
  "files": [
    { "kind": "preview",
      "path": "Projects/26-173 - studio JEFRE llc - Curtain Sculpture Blue Translucent with Lighting/06-Production Documents/slicer/2026_0706 - Curtain Wall For Print preview.png",
      "bytes": 1507087 },
    { "kind": "workspace",
      "path": "Projects/26-173 - studio JEFRE llc - Curtain Sculpture Blue Translucent with Lighting/06-Production Documents/2026_0706 - Curtain Wall For Print.mass",
      "bytes": 313560625 }
  ]
}
```
- `stats.printTime` / `stats.weight` are display strings; all stats optional
  (nulls omitted).
- `files[].kind` today: `"preview"` (viewport PNG the slicer renders into a
  `slicer/` folder beside the `.mass`) and `"workspace"`; `"krl"` (the exported
  `.src`) is planned.
- `files[].path` is **UNAS share-relative**: the slicer strips its local mount
  prefix `/Volumes/MassiveFILES/`, so paths start at `Projects/…`.
  **Open question for the ERP:** confirm the root its UNAS API resolves against
  matches this (i.e. the MassiveFILES share root). If it needs a different base,
  say which and the slicer will adjust its prefix stripping.

Response — 201; the ERP assigns the rev (slicer reads `rev|revision|revNumber`,
`url|link` optional and used for a future deep-link):
```json
{ "rev": 3, "url": "https://lab.massivemake.com/projects/498/elements/41?rev=3" }
```

## Slicer-side state (already shipped)

- `.mass` workspaces persist the attachment (`type/id/number/title/elementId?/
  elementName?`) and auto-connect + restore it on open.
- Dock UI: search/attach, element picker, create-element (both flows), and
  "Send Slice to ERP" which renders the preview PNG and POSTs the payload above.
- A mock implementing exactly this contract lives in the session scratchpad
  (`mock_erp.py`) and the whole flow is covered by unit tests
  (`src/MassiveSlicer.Tests/ErpParsingTest.cs`).
