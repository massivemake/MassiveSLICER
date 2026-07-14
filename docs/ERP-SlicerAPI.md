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

**Answered (ERP #961):** leads can't own elements — the ERP returns a 400 with a
human-readable message the slicer surfaces verbatim. When the lead was already
converted, the message names the project number and the body carries the linked
project's id (`projectId`); the slicer parses it and offers a one-click
"Attach to Converted Project" that resolves the project via the elements
endpoint's envelope and re-attaches the workspace.

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
  **Answered (ERP #961):** confirmed — the UNAS path root is the MassiveFILES
  share root; the slicer's `Projects/…` prefix-stripped paths are correct as-is.

Response — 201; the ERP assigns the rev (slicer reads `rev|revision|revNumber`,
`url|link` optional and used for a future deep-link):
```json
{ "rev": 3, "url": "https://lab.massivemake.com/projects/498/elements/41?rev=3" }
```

## Sent-to-robot notification (slicer → ERP, shipped slicer-side)

When "Export to Robot" uploads a program straight onto the cell controller's
D drive (SMB), the slicer registers a slice rev on the linked element with an
additional top-level `sentToRobot` block, and mirrors the same `.src` into the
project's `slicer/` folder on the UNAS (referenced as a `files[]` entry with
`"kind": "krl"`):

```json
{
  "stats": { "...": "as above" },
  "files": [
    { "kind": "krl",       "path": "Projects/.../06-Production Documents/slicer/2026_0706 - ... .src", "bytes": 278396928 },
    { "kind": "workspace", "path": "Projects/.../06-Production Documents/... .mass", "bytes": 313560625 }
  ],
  "sentToRobot": {
    "cell": "LFAM 2",
    "host": "192.168.0.152",
    "file": "2026_0706 - Curtain Wall For Print.src",
    "robotPath": "\\\\192.168.0.152\\2026_0706 - Curtain Wall For Print.src",
    "at": "2026-07-07T18:20:00.000Z"
  }
}
```

The ERP can treat a rev carrying `sentToRobot` as "program is on the printer,
ready to run" — e.g. set the slice status accordingly and show the cell name.
Unknown fields are safe to ignore until that lands.

## Pricing sync (live at /api/slicer/v1 — slicer support shipped)

The ERP is the source of truth for all pricing; the slicer never hard-codes
rates, material prices, or markup.

### GET /pricing

Returns the pricing config: `version` (hash — changes when any pricing number
changes), machine rates (`effectiveRatePerHour`,
`effectiveRateWithFinishingPerHour`), the active `materials` catalog
(`costPerKg`/`costPerLb`/`density`), `markup` (overhead + profit rates), and
`quantityDiscounts` (`minQuantity` + `rate`).

Slicer behavior: fetched on every successful connect and re-fetched whenever a
quote/costing echoes a different `pricingVersion`. The cached config drives the
live cost line in the stats panel (shown as "(ERP est.)"); rates appear in the
ERP dock. Console: `erp pricing` prints the full cached catalog.

### POST /quote

Body: `printTimeSec` and/or `weightKg` (at least one resolvable or 400),
optional `material` (name), `quantity` (triggers discounts), `finishing`,
`customMachineRatePerHour`. Response: `machineCost`, `materialCost`,
`quantityDiscount`, `markup`, `subtotalCost` (internal — never client-facing),
`clientPrice` (the customer number), `pricingVersion`.

Slicer behavior: `erp quote [qty] [finishing]` posts the current slice stats and
prints the authoritative breakdown. Slice registration also now sends numeric
`stats.printTimeSec` + `stats.weightKg` alongside the display strings.

### Costing on slice registration

`POST /slices` 201 responses include a `costing` block (same shape as a quote,
quantity 1, no finishing) — the permanent cost record for that rev. The slicer
shows the client price in the dock status and console after `sendslice`, and
uses the echoed `pricingVersion` to detect stale configs.

## Slicer-side state (already shipped)

- `.mass` workspaces persist the attachment (`type/id/number/title/elementId?/
  elementName?`) and auto-connect + restore it on open.
- Dock UI: search/attach, element picker, create-element (both flows), and
  "Send Slice to ERP" which renders the preview PNG and POSTs the payload above.
- A mock implementing exactly this contract lives in the session scratchpad
  (`mock_erp.py`) and the whole flow is covered by unit tests
  (`src/MassiveSlicer.Tests/ErpParsingTest.cs`).
