# memory.md archive — older history

Split out of `memory.md` on 2026-07-27 so the live file stays small enough to skim at the
start of every session. **Nothing was deleted** — this is the verbatim tail. Current history
and durable conventions stay in `memory.md`; come here only when digging into a specific
past decision.

Contents: the June-2026 test-status snapshot, session changelog entries for 2026-07-04 and
earlier, and the build 1–30 log.

---

## Test status snapshot (last verified 2026-06-21 — superseded by `docs/KNOWN-TEST-FAILURES.md`)

## Test status (last verified 2026-06-21)

| Test | Result |
|------|--------|
| `CellSceneLoadTest` — LFAM2 robot+bed | Pass |
| `CellSceneLoadTest` — LFAM3 robot+rotary | Pass |
| `Lfam3LoadTest` — all GLBs incl. `affecto_staubli.glb` | Pass |
| `GltfImportTest` — crystal GLB | Pass |

### Expected healthy console on LFAM 2 boot

```
[cell] discovered 2 cell(s).
[cell] loading lfam2…
[cell] LFAM 2: robot=True bed=True env=… tools=… rotary=False — CPU ready in …ms
[bed] LFAM 2: visualOffset=(…)  BASE marker=(…)  visual grid=(…)
[cell] scene swap applied — robot visible
```

No `Failed to load 'lfam2.json': … different thread owns it.`

### LFAM 3 notes
- `lfam3.json`: `"bed": { "hidden": true }` — flat bed omitted; **rotary bed** expected in environment nodes (`RotaryBed`).
- Console: `robot=True bed=False rotary=True` is normal for LFAM 3.

### LFAM 3 — shop-floor jog directions (verified on robot 2026-06-25)

**Use this vocabulary for console `move-pose` / agent bridge commands** — NOT generic “math right = +X”.

| User says | Axis delta (tool #6, base #0) | Verified |
|-----------|-------------------------------|----------|
| **Forward** | **+X** (+304.8 mm per foot) | ✓ user confirmed +X was forward |
| **Back** | **−X** | (inverse of forward) |
| **Right** | **+Y** | ✓ (inverse of −Y left) |
| **Left** | **−Y** | ✓ user confirmed −Y was left |
| **Up** | **+Z** | |
| **Down** | **−Z** | ✓ 1′ down 2222.8 → 1918.0 mm |

1 foot = **304.8 mm** in KUKA `$POS_ACT` (mm).

### LFAM 3 — MS_* app motion (C3 + `cell.src`, 2026-06-25)

**Working on LFAM3 @ 192.168.0.153** — scanner **already mounted** (do **not** use `scan-pick` / `Scanner_Pick` for jogging).

| Item | Detail |
|------|--------|
| Bridge | `LocalControlBridge` @ `127.0.0.1:8723`, port in `%LOCALAPPDATA%\MassiveSlicer\bridge.port` |
| Commands | `sync`, `cell LFAM 3`, `pos`, `joints`, `readvar`, `set-frame`, `move-home`, `move-pose …`, `move-joints …`, **relative:** `move-up/down/forward/back/right/left [dist]`, `move up 1'` |
| Handshake fix | `InitCommandServerAsync` seeds from **`MS_SEQ`** not `MS_ACK` |
| `cell.src` | CASE 1: `PTP MS_POSE` (no HOME S/T pin); CASE 5: `set-frame` only |
| Frame rule | **`pos` prints `ctrl tool #N, base #M`** — copy the full `move-pose … 20 N M` line; app LFAM3 label (base #3) may ≠ controller `$ACT_BASE` |
| Typical scanner jog frame | **tool #6, base #0** on controller (`set-frame 6 0` if needed) |
| `lfam3.json` scanner **dock** pose | Stand pickup only — **not** for mounted-scanner jogging |

**Agent rule:** Before Cartesian jog, run `pos` or `readvar $ACT_TOOL $ACT_BASE`. Use **LFAM 3 shop-floor table above** for direction words.

**Cartesian down from HOME-area scanner pose** (`Z≈1918`, tool #6): `move-pose` **−Z** can fault **“Software limit switch +A6”** — IK pins ABC and needs A6 past soft limit. **Workflow:** run `joints` (reads `$AXIS_ACT`), plan `move-joints` tweaking **A2/A3/A5** instead of Cartesian down. `readvar $AXIS_ACT` works today; `joints` + `move-joints` added 2026-06-25.

### LFAM 3 — logged TCP poses (tool #6, base #0 unless noted)

Captured live via bridge during MS_* bring-up (2026-06-25). ABC in degrees.

| Name | X | Y | Z | A | B | C | Notes |
|------|---|---|---|---|---|---|-------|
| **HOME** (scanner on) | 2520.3 | −75.7 | 2222.8 | −95.997 | −0.397 | −90.093 | After `move-home` ack |
| **HOME −1′ Z** | 2520.3 | −75.7 | 1918.0 | −95.997 | −0.397 | −90.094 | Down 1′ — exact |
| **HOME +1′ X (forward)** | 2825.1 | −75.7 | 1918.0 | −95.997 | −0.397 | −90.094 | +304.8 mm X |
| **After −1′ Y attempt** | 2618.6 | −324.6 | 1501.4 | −100.719 | −2.364 | −117.957 | IK/path; not commanded Cartesian |
| **Pre-home sync pose** | 2175.6 | −264.6 | 1448.3 | −31.265 | 88.578 | −27.720 | App status tool #6 base #3 |
| **Scanner down ~24″ bed** | 2093.6 | −17.2 | 131.6 | −90.057 | −2.310 | 179.710 | User-teach 2026-06-25; tool #6 base #0; scanner aimed down |

**Scanner down ~24″ bed — `$AXIS_ACT`:** A1=1.66 A2=−66.75 A3=109.43 A4=−180.19 A5=−46.19 A6=197.00 E1=0

Copy-paste (scanner down ~24″ bed):
```
move-pose 2093.6 -17.2 131.6 -90.057 -2.310 179.710 20 6 0
move-joints 1.66 -66.75 109.43 -180.19 -46.19 197.00 0 20 6 0
```

Copy-paste template (replace XYZABC):
```
move-pose <X> <Y> <Z> <A> <B> <C> 20 6 0
```

### LFAM 3 KUKA joint limits & cell poses

**Joint limit source (KRC4):** `\\192.168.0.153\krc\ROBOTER\KRC\R1\Mada\$machine.dat` — `$SOFTN_END[1..7]` (min), `$SOFTP_END[1..7]` (max).

| Axis | Min (°) | Max (°) |
|------|---------|---------|
| A1 | −185 | +185 |
| A2 | −140 | **−5** |
| A3 | −120 | +168 |
| A4 | −350 | +350 |
| A5 | −125 | +125 |
| A6 | −350 | +350 |
| E1 | −185 | +185 |

Synced into `lfam3.json` `robot.joints[]` (A1–A6 only; E1 is rotary bed axis).

**Cell load priority:** `CellPaths.PreferredCellsDirectory()` prefers repo `assets/cells/` (NAS) over `%LOCALAPPDATA%\MassiveSlicer\assets\cells` and publish folder. Dev-tuned stand poses live in AppData; repo copy must stay complete or geometry disappears.

**Robot:** `worldPosition` (0, 0, 1000); homes — Home [0,−90,90,0,15,0], Service [0,−90,110,0,35,0].

**Rotary bed** (`rotaryBed`):
- `basePos`: [2048.242, 63.63916, −1090.5845] mm
- `baseAbc`: [0, 0, −90]°
- `e1Sign`: 1

**Stands** (AppData dev-tuned, metres + radians):

| id | position [x,y,z] | rotation [x,y,z] |
|----|------------------|------------------|
| extruder | 0.236, 0.26573, 2.34092 | −π/2, 0, 0 |
| scanner | 1.34, 0.26573, 2.12689 | π/2, ~0, −π |
| spindle | −0.769, 0.26573, 2.37767 | −π/2, 0, 0 |

**Tool docks** (KRL mm/deg):

| Tool | dock (x,y,z,a,b,c) | krlIndex |
|------|-------------------|----------|
| HV Extruder | 236.37, −2633.39, −545.53, 28.06, 88.73, 117.37 | 2 |
| Scanner | 1340.33, −2028.61, −100.89, −75.55, −0.51, −179.71 | 6 |
| Spindle | −768.61, −2027.87, −219.42, −52.25, 89.67, −53.10 | 3 |

**Tool TCPs:** Extruder/Spindle share 694.76, 17.74, 312.44, A15; Scanner has distinct TCP + sensor origin.

**Bridge:** `bridgeIp` 192.168.0.153, port 7000; `extIp` 192.168.0.196.

---

---

## Session changelog — 2026-07-04 and earlier

### 2026-07-04 — Branch convention (agreed with Thom)
- **`master` = integration branch (GitHub default), Thom merges everything there.**
- **Mac side works on and pushes to `main`.** Sync = `git merge origin/master` into `main` (fast-forwards when both sides merge regularly). As of today both branches point at the same commit (`2b6d55b`).
- Also today: curtain print failure root cause **corrected — truncated program transfer, not singularity.** The production-share .src ends mid-line at Z 2154 (layer 718 of 1047), matching the physical print height and the nozzle-drool blob at the final preview pose. TCP auto-rotation (build 27) remains valuable but the immediate prevention is transfer verification on export/Send-to-Robot (planned).

### 2026-07-04 — Curtain print failure countermeasures (builds 25–30)
**Print failed mid-run: KUKA hit wrist singularity.** Root cause: KRL export wrote a frozen `A 0, B 90, C 0` orientation on every move — the exporter's gimbal-lock branch zeroed any TCP rotation for a straight-down tool.
- **TCP auto-rotation repair (build 27):** post-slice validation now searches the smallest print-neutral nozzle spin (rotationally symmetric tool) that clears flagged wrist configurations, ramps it in/out over ~60 moves, re-verifies with IK, and bakes per-move `ToolpathMove.TcpYawDeg` into KRL export (`KrlExporter.KukaAbc` now composes the spin and preserves it at gimbal lock).
- **Validation is loud (26/28/29):** red ⚠ alert in the bottom status bar with singular/unreachable counts + Z range, "Go to issue" jumps the scrubber to the first flagged move; Export KRL / Send to Robot show a confirm dialog (Cancel / Go to issue / Export anyway).
- **Material flow calibration (25):** Material Preset dialog gains a guided purge-and-weigh section → per-material `FlowRate` (rev/cm³) with provenance. Fixes wrong starting RPM from the uncalibrated default 0.463 (the over-extrusion "creep" that smeared the curtain's bottom third; RPM was hand-tuned mid-print). `RPM% = W×H×v×FlowRate×60` already existed in `KrlAnout` — the constant was never measured per material.
- **UI (29/30, CEO request):** no popups — progress (real 0–100%) and alerts render inline in the bottom status bar; status shows loaded workspace filename.
- Key files: `KrlExporter.cs`, `KrlAnout.cs`, `ToolpathMove.cs`, `MaterialPreset.cs`, `MaterialPresetDialog.axaml(+VM)`, `ViewportView.axaml.cs` (validation/repair), `BottomLeftDockView.axaml`.

### 2026-07-03 — Sine wave Fixed/Dynamic + trustworthy bead preview (builds 17–24)
- **Wave texture bands diagnosed:** Fixed (seam-anchored) phase drifts as the cross-section morphs; bands appear where seam-to-point arc grows ~1 wavelength/layer (verified: predicted vs measured crest shift at both user-reported bands). `tools/wave_analysis.py` measures phase coherence in exported .src.
- **Fixed / Dynamic phase methods (21–22):** "Phase" dropdown in wave settings. Fixed = original, byte-identical (verified vs printed program). Dynamic = phase inheritance from the layer below + stagger — constant layer-to-layer crest shift, shape change absorbed as bounded wavelength flex. Roughly halves worst-height error; means near zero.
- **Bead renderer rebuilt (19):** indexed mesh + chord-error decimation (0.35 mm) — full 2.7M-move wave toolpaths render faithfully; old fixed-step decimation cut chords across whole wavelengths (the "inconsistent sine wave" was the preview lying, not the toolpath). Overlays share the bead EBO (fixed latent 2.3 GB alloc).
- **Live bead color (16):** picker next to Show Bead, shader-uniform driven, no re-slice; blue came from stale `prefs.json` + material fallback, all defaults now white.
- **Progress + save fixes (23–24):** real 0–100% progress (byte-accurate workspace read, per-layer planar slice), load off the UI thread; doubled save extensions (`.src.src`/`.mass.mass`) fixed everywhere; macOS app bundle + Dock icon (`tools/make_macos_app.sh`).
- **Scrub freeze fix (20):** repaint no longer depends on a successful IK solve.

### 2026-06-30…07-02 — macOS Sine+Show-bead crash hunt (builds 1–16)
- **SIGABRT on slice with Sine wave + Show bead:** unhandled managed exceptions escaped to the native GL render loop (CLR abort). `SliceLogger` (→ `~/Desktop/massiveslicer-slice.log`) + try-catch on the GL upload path exposed the real error: bead VBO `IndexOutOfRange` — decimation counted moves globally but selected per layer (undersized array). Also ~390 MB bead allocations for wave toolpaths → decimation budget.
- Build numbers introduced (`BuildInfo.cs`, shown in status bar). Full build-by-build log: **Build log** section at the bottom of this file.

### 2026-06-25 — Milestone: KRL import toolpath + viewport polish (GitHub `feature/print-scan-mill`)
**Milestone title (GitHub):** `KRL Import & Viewport Polish — June 2026`

**Shipped**
- **KRL import → scrubbable toolpath:** `KrlToolpathParser`, `ImportKrlToolpath`, outliner node under rotary/print object, auto-select on GL upload.
- **KRL scrub crash fixes:** (1) `ComputeMovePrefixSums` — Mill in extrude bucket not travel; (2) `UploadBead` early-return left empty `_beadVertexCumulative` → `IndexOutOfRangeException` on scrub; (3) `ScrubCount` clamp + bounds check.
- **KRL selection:** `PickToolpath` includes Mill/Travel; centroid includes Mill; nested outliner `SuppressNextOutlinerListBoxSelection` prevents ListBox overriding child click.
- **Show Bead for KRL:** `UploadBead`, `BuildBeadVertexCumulative`, `BuildBeadColoredData`, overhang scoring — all use `ToolpathMoveKinds.IsCutSegment` (Mill + Extrude).
- **PBR material MCP:** full layer toggles + factor overrides on bridge `/materials` + MCP tools.
- **Outliner:** hide Delete on robot/rotary/stands/print bed; recursive `OutlinerItemView` for nested toolpaths/scans.
- **Diagnostics:** full-window screenshot capture (`app_YYYYMMDD_HHmmss.png`).
- **Tests added:** `KrlToolpathHandlingTest`, `KrlImportOutlinerTest`, `PbrMaterialSettingsTest`, `OutlinerCanDeleteTest`, `PickerTest`, plus rotary/scan outliner tests.

**Key files touched**
- `src/MassiveSlicer.Core/IO/KrlToolpathParser.cs`, `Models/ToolpathMove.cs` (`ToolpathMoveKinds`)
- `src/MassiveSlicer.Viewport/Rendering/ToolpathRenderer.cs`, `SceneRenderer.cs`
- `src/MassiveSlicer.App/Views/ViewportView.axaml.cs`, `OutlinerItemView.axaml.cs`, `LeftPanelView.axaml.cs`
- `src/MassiveSlicer.App/ViewModels/ViewportViewModel.cs`, `MainWindowViewModel.cs`
- `src/MassiveSlicer.App/Console/PbrMaterialBridge.cs`, `scripts/mcp/massiveslicer_mcp.py`
- `src/MassiveSlicer.App/AppScreenshotCapture.cs`

**Verify after pull**
```powershell
dotnet test --filter "FullyQualifiedName~KrlToolpathHandlingTest|FullyQualifiedName~KrlImportOutlinerTest|FullyQualifiedName~PbrMaterialSettingsTest|FullyQualifiedName~OutlinerCanDeleteTest"
Stop-Process -Name "MassiveSlicer.App" -Force -ErrorAction SilentlyContinue
Set-Location '\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER V2'
dotnet publish 'src/MassiveSlicer.App/MassiveSlicer.App.csproj' -c Release -o "$env:LOCALAPPDATA\MassiveSlicer\build"
```
Then: import a `.src` with inline LIN frames → select in outliner → scrub + toggle Show Bead.

### 2026-06-25 — LFAM3 live MS_* motion + jog axis learning
- **Unblocked `move-home`:** `InitCommandServerAsync` seeds `_msSeq` from `MS_SEQ` (was `MS_ACK` → seq collision).
- **Bridge commands:** `readvar`, `set-frame` (MS_CMD=5), `move-pose … [tool] [base]`; `pos` reads `$ACT_TOOL`/`$ACT_BASE` and appends frame suffix.
- **`cell.src`:** CASE 1 `PTP MS_POSE` without HOME S/T pin; CASE 5 set-frame-only.
- **Verified via bridge:** `move-home` ack; hold-pose; down 1′ (−Z); forward 1′ (+X).
- **Jog vocabulary (user-corrected):** forward **+X**, right **+Y**, left **−Y**, down **−Z** — logged in **LFAM 3 — shop-floor jog directions**.
- **Frame trap:** `pos` may show coords in `$ACT_BASE=0` while app says base #3 — always copy `move-pose … tool base` from `pos`.
- **Do not use** `lfam3.json` scanner dock or `scan-pick` when scanner already on robot.
- Poses logged in **LFAM 3 — logged TCP poses** table above.
- **+A6 soft limit:** Cartesian down from HOME scanner area faults; use `joints` / `move-joints` for joint-space planning.
- **New commands:** `joints` (`$AXIS_ACT` + limits), `move-joints` (MS_CMD=3 / `MS_AXIS`).
- **Relative jog (2026-06-25):** `move-up 1'`, `move down 12in`, `move forward 100mm` — distances → mm; omit distance = **1′**; LFAM3 axes per shop-floor table.

### 2026-06-22 — Multi-axis displaced-surface milling pipeline (session 8)
- **Direction correction:** the real goal is NOT carving a flat 2D grayscale image. It is milling the **actual surface of an imported low-poly PBR model**, recovering detail from its **normal/displacement/bump/height maps** via UV, with a user-set **displacement distance**, **multi-axis** (spindle tilts to follow surface normals), plus an **after-the-fact fail-rate %** (how much the tool gouges/leaves vs the ideal). The Phase-1 image-relief path (session 7) still exists but is not the primary flow.
- **Pipeline (all Core, all unit-tested):**
  - `Models/HeightField2D` — UV-space scalar field, bilinear wrap sampling.
  - `Slicing/NormalMapIntegrator` — Poisson (red-black SOR) integrate a tangent-space normal map -> relative height (glTF has no displacement channel, so the normal map is the embedded source). Round-trips a Gaussian bump to <0.05 MAE.
  - `Slicing/DisplacedSurfaceBuilder` — adaptively subdivide the low-poly mesh to map texel density, push each vertex along its normal by `height(uv)*distance`, recompute normals.
  - `Slicing/SurfaceFollowMillGenerator` — raster the displaced surface top-down (uniform XY triangle grid); ball-nose tip rides each contact, tool axis = interpolated surface normal carried on `ToolpathMove.Normal` (no model change — that field already existed as the KRL orientation fallback). Boustrophedon + safe-Z retracts. v1 is a single top-down drive; wrap-around walls/undercuts is future.
  - `IO/KrlExporter.WriteMillBody` — per-move A/B/C from `move.Normal` when set (else layer normal). Verified: flat -> ABC (0,90,0); slope-0.3 plane -> (0,73.3,0) = reoriented by exactly 16.7deg.
  - `Slicing/ToolpathSurfaceDeviation` — gouge/residual fail-rate %: signed distance from each ideal-surface sample to nearest ball sphere (contact+r*axis); inside beyond tol = gouge, outside beyond tol = residual. Flat -> 0% gouge; finer stepover -> less residual + lower cusp.
- **App:** `Services/PbrHeightFieldFactory` (public) builds the height field from a supplied displacement/bump/height image, else integrates the model's normal map (samples `TextureData.Pixels`, CPU-side). `ViewportView.ComputeDisplacedSurfaceAsync` shared by **Preview Displaced Surface** (adds a textured displaced mesh) and **Generate Multi-Axis Toolpath** (registers a toolpath node + runs the analysis). New SUBTRACTIVE/MILLING controls: displacement distance, analysis tolerance, the two buttons, and a fail-rate readout. Commands on ViewportViewModel mirror MillCommand.
- **Real-data proof (test):** the crystal GLB (266-vert low-poly + 4096^2 normal map) -> 32,176-vert / 53,360-tri displaced surface, displacement 0..5mm bounded. All works headlessly.
- **Branch:** renamed `feature/scan-rotary-bed-calibration` -> `feature/print-scan-mill` (old remote deleted by user). 6 session-8 commits da146a3..f173bea; later ones committed LOCALLY (not pushed yet).
- **lfam3.json corruption — ROOT-CAUSED + FIXED.** Robot/bed vanished because cell saves fanned out to the repo: `CellPaths.WriteTargetsFor` mirrored any write whose path merely contained `/assets/cells/` to the hardcoded NAS repo root + all source trees. `CellDevTransformSaveTest` writes a minimal `{modelPath:"robot.glb", joints:[]}` cell to a temp path, so running the **test suite** (or the app's Save View / bed-calibration saves) overwrote the real `lfam3.json` with that stub. Fix: `CellPaths.MirrorToSourceTrees` (default **off**; opt in via `MASSIVE_SLICER_MIRROR_CELLS=1`) gates the fan-out — ordinary writes now touch only the primary file. Also removed the test's leaked `Directory.SetCurrentDirectory`. Result: repo stays clean across full test runs, and 2 formerly-"pre-existing" failures (CellSceneLoad, MultiToolDock) were self-inflicted by the corruption and now pass. Remaining 5 failures are genuinely pre-existing (KrlExporter ×2 stale, Meshopt ×2 missing reference GLBs, Lfam3MillingConfig expectation). See [[lfam3-json-corruption]].

### 2026-06-22 — Phase 1: heightmap relief milling (subtractive), end-to-end (session 7)
- **Goal (the real use case):** 3D-print an oversized blank, then **mill** detail back into it with the robot spindle (**HSD ES951** head, interchangeable bits e.g. 10mm ball mill). A grayscale **relief/heightmap** is the single source of truth (white = high surface at referenceZ, black = `HeightScaleMm` deeper). We sample the relief at **stepover resolution** — never a high-res displaced mesh (that's the crash risk the user flagged).
- **New Core:** `Models/ReliefMap.cs` (decode-agnostic heightfield: `Samples` row-major bottom-row-first, `Cols/Rows`, `OriginX/Y`, `WidthMm/LengthMm`, `HeightScaleMm`, `Invert`, `ReferencePlaneZ`; `SurfaceZAt`/`SampleSurfaceZ` bilinear, NaN outside), `Models/MillSettings.cs` (`ToolEndType{Ball,Flat}`, diameter, stepover/stepdown, finish allowance, feeds, rapid Z, spindle RPM, max depth), `Slicing/ReliefMillSlicer.cs` (`Slice(ReliefMap,MillSettings)->Toolpath`).
- **Anti-gouge inverse offset (the crux):** tip Z = max over the tool-radius disk of `target + (r - sqrt(r^2 - d^2))` for Ball (max(target) for Flat) — a wide ball physically can't dip into a narrow pit. Unit-tested.
- **Toolpath model:** added `MoveKind.Mill`. Roughing = descending Z-level floors leaving finish allowance; finish = boustrophedon raster on the offset surface. Cuts = `Mill`; repositioning/plunge = `Travel`+`IsZHop` (a plunge emitted as Mill once inflated maxZ — fixed).
- **KRL export:** `KrlExporter` parameterized with `IsMilling/SpindleRpm/CuttingFeedMmMin/PlungeFeedMmMin`; mill branch writes a spindle program (`TOOL_NO`=spindle index, feed -> `$VEL.CP` = mm/min / 60000, rapids at RapidZ) with **no** extruder `$ANOUT[1]`(temp)/`$ANOUT[4]`(RPM). Spindle on/off stays in the editable header/footer template (KUKA 0-10V analog -> ATV340 VFD). `WriteKrlAsync` detects a mill toolpath (`Layers.Any(... Kind==Mill)`) and builds mill settings from `SubtractiveSettings` + spindle tool `KrlIndex` from `cell.EffectiveTools`.
- **UI:** relief-mill controls live in the **SUBTRACTIVE tab** (heightmap+Browse, height scale, invert, tool diameter, ball/flat, stepover, stepdown, finish allowance, feeds, rapid Z, RPM, ref-Z auto/manual, footprint auto, Generate/Export/Send). **GOTCHA fixed this session:** the Milling phase auto-selects the SUBTRACTIVE tab (`MainWindowViewModel` ~1004), but the controls were first built in the TOOLPATH tab's MILL expander -> user landed on a "Coming soon" stub. Moved them into the SUBTRACTIVE tab; removed the TOOLPATH duplicate. **Rule: per-phase landing tab = Print->Additive, Scan->Scan, Mill->Subtractive — put a phase's primary controls on its landing tab.**
- **Verified:** full clean build + publish; app launches; **3/3 tests pass** incl. a headless end-to-end `ReliefMap -> ReliefMillSlicer -> KrlExporter` test asserting a real spindle program (TOOL_NO, `$VEL.CP`, no extruder `$ANOUT`, carved Z within `[-scale,0]`); and the SUBTRACTIVE MILLING panel renders on the Milling phase with **all bindings resolving** (defaults visible). Test fixture: `assets/test/test_relief.png` (radial dome). **Not yet done live:** import a blank + Browse heightmap + Generate -> orange toolpath render (manual step; renderer already maps `Mill` to orange `_millColor`).
- **Next (Phase 2/3):** scan-surface projection replaces flat referenceZ with `scanSurfaceZ(x,y)`; additive stock = relief shape + uniform allowance (Clipper `InsetContour2D` outward offset + relief-raised top) so the printed blank always envelopes final+allowance.

### 2026-06-21 — Real PBR rendering + material debug channels (session 6)
- **Metallic-roughness PBR with textures.** Imported GLBs now render base colour, metallic-roughness, normal, AO, and emissive maps with a Cook-Torrance BRDF + env IBL (ACES tonemap). Verified on `crystal_stone_rock(1).glb` — textured Final Render matches a reference glTF viewer.
- **New data model:** `Scene/TextureData.cs` (+ `TextureWrapKind`), `Scene/MaterialData.cs` (+ `AlphaMode`); `MeshData` gained nullable `Uvs`/`Tangents`/`Material` via a ctor overload (old ctor delegates with nulls — all existing loaders unchanged). `CloneMeshData` passes them through reusing refs.
- **Loader:** `GltfLoader.ExtractPrimitive` reads `TEXCOORD_0` + `TANGENT` (computes tangents via Lengyel when absent), decodes embedded PNG/JPEG via StbImageSharp, dedups images by `Image.LogicalIndex`, sets `node.CullFaces` from `DoubleSided`. Correct sRGB (baseColor/emissive) vs linear (normal/MR/AO) flags.
- **GPU:** `MeshRenderer.Upload` now interleaves 12 floats (pos3+nrm3+uv2+tan4); new `Rendering/GpuTextureCache.cs` (ref-counted, keyed by `TextureData.CacheId`, sRGB vs RGBA8, mipmaps) mirrors `GpuMeshCache`; material maps bound to units 4-8 (1=env,2=heatmap,3=boundary).
- **Shader (uber, single program):** `MeshRenderer.FragSrc` mode 0 = Cook-Torrance (GGX/Smith/Schlick, F0=mix(0.04,albedo,metal)) + normal mapping via TBN + IBL (env diffuse high-LOD, specular roughness→LOD + analytic Karis EnvBRDF) + ACES. Mask discard + double-sided `gl_FrontFacing` flip. Modes 1/2/3 (normals/layer/fastcell) untouched; presets route through the factor path with `SuppressTextures`.
- **Material debug channels:** `ShaderMode` += BaseColor/Metalness/Roughness/NormalMap/AO/Emission/UvChecker (shader modes 4-10, early-return raw-channel branches; UV checker procedural, magenta when no UVs). Wired in `SceneRenderer.ApplyShaderModeToSubtree` + a **MATERIAL CHANNELS** section in `LeftPanelView` (Viewport tab). All verified live (Standard, Base Color, UV Checker, Normal).
- **GOTCHA — stale incremental build on the NAS:** a "white/untextured" render turned out to be a stale `MassiveSlicer.Viewport.dll` from incremental MSBuild on the network share. `dotnet build … --no-incremental` (clean) fixed it. If a Viewport render change "doesn't take", clean-rebuild Viewport before debugging further.
- **Note:** `ActiveShaderMode` persists across launches (AppPreferences) — left on **Standard** so Final Render is the default.
- Files: `Scene/TextureData.cs`, `Scene/MaterialData.cs`, `Scene/MeshData.cs`, `Loading/GltfLoader.cs`, `Rendering/MeshRenderer.cs`, `Rendering/GpuTextureCache.cs`, `SceneRenderer.cs`, `ShaderMode.cs`, `Views/LeftPanelView.axaml`, `Views/ViewportView.axaml.cs` (`CloneMeshData`); test in `GltfImportTest.cs`. Published to `%LOCALAPPDATA%\MassiveSlicer\build`.

### 2026-06-21 — Workflow polish: lift pills, clear toolpath on close, connector ends at rivet (session 6)
- **Header caret direction:** `Lfam3WorkflowMinimizeIcon` (ViewportViewModel) was hardcoded `mdi-chevron-up`; now `IsLfam3WorkflowExpanded ? mdi-chevron-down : mdi-chevron-up` (down = collapse when expanded).
- **Pills up ~20px:** `Lfam3WorkflowPhaseColumn.axaml` `Canvas.Bottom` 58 → 78.
- **Close menu hides toolpath:** `CollapseToolChangePlayback` now calls `ClearToolChangeSequence()` (was only hiding the strip) → viewport path overlay/markers cleared, prior tool restored, pills deactivated.
- **Connector line:** added opaque 54px disc (`#0A0E14`) behind the node ellipse in `Lfam3WorkflowPhaseBlock.axaml` so the line terminates at the circle perimeter (no show-through on active/pending/completed rivets).
- **Verified** all three live via screenshots (Pick click + collapse chevron). Published: `%LOCALAPPDATA%\MassiveSlicer\build`

### 2026-06-21 — Workflow Pick/Deposit menu opens UPWARD (session 6)
- **Symptom:** Clicking Pick/Deposit on an active LFAM 3 phase expanded the playback strip + param card **downward** — covering the phase rivet and running off the bottom of the screen behind the status bar/taskbar.
- **Cause:** Float was a `Border` (`VerticalAlignment="Bottom"`, fixed `TranslateTransform Y=-74`) inside the **56px** phase-column cell. When expanded content (~240px) exceeds the cell, Avalonia clamps the arranged height to 56 and **top-anchors** it, so the StackPanel overflows downward; the fixed −74 lift was far too small. Verified live via screenshots (UIA-driven Pick click).
- **Fix:** `Lfam3WorkflowPhaseColumn.axaml` — host the float in a **`Canvas`** (measures children with infinite height → no clamp). `Canvas.Bottom="58"` pins the float bottom ~2px above the rivet; float `Border Width="{Binding #FloatCanvas.Bounds.Width}"`, inner `StackPanel HorizontalAlignment="Center"`. Stack order pills(bottom)→card→playback grows strictly upward. No converter / code-behind needed.
- **Verified:** Build + run on this machine; expanded menu sits fully above the rivet, on-screen; rivet still aligned on the connector line.
- Published: `%LOCALAPPDATA%\MassiveSlicer\build`

### 2026-06-21 — memory: workflow layout rules consolidated (session 5)
- Documented canonical split: `PhaseBlock` (rivet) vs `PhaseColumn` (floats) vs `PickDepositFloat` (pills).
- Added **LFAM 3 workflow layout — do not regress** table + never/always rules.
- Marked rivet alignment user-verified; Pick/Deposit fix published, awaiting user confirm on pill visibility.

### 2026-06-21 — Workflow Pick/Deposit floats + icon alignment (session 4)
- **Symptom:** Phase rivets aligned on connector; Pick/Deposit pills invisible.
- **Cause:** Float `Border` used `Margin="0,0,0,62"` inside a 56px-tall column — layout allocated negative height → 0px float.
- **Fix:**
  - `Lfam3WorkflowPhaseColumn.axaml`: floats use `TranslateTransform Y=-74` (visual lift) instead of oversized bottom margin; rivet unchanged in `Lfam3WorkflowPhaseBlock`.
  - `Lfam3WorkflowTimelineView.axaml`: 90px spacer + negative margin above track for float paint room; 4-phase grid migrated to `Lfam3WorkflowPhaseColumn` (overlay removed).
  - `ViewportViewModel.cs`: phase detail expansion gated on `ToolPanel.ShowPlayback` (Pick/Deposit), not `LiveIo.IsExpanded`.
- **Rule:** Never share layout tree between rivet row and floats; never bottom-margin floats beyond cell height.
- Published: `%LOCALAPPDATA%\MassiveSlicer\build`

### 2026-06-21 — Pick/Deposit layout regression attempts (superseded)
- **Symptom:** Pick/Deposit pills vanished when moved “up” via large negative bottom margins inside the 56px phase block.
- **Failed approaches:** `Margin bottom: 148` inside `Lfam3WorkflowPhaseBlock`; bottom-anchored stack in phase block (shifted rivets); track-level `Lfam3WorkflowPickDepositOverlay` (icons OK, pills still invisible).
- **Superseded by:** `Lfam3WorkflowPhaseColumn` + `TranslateTransform` lift + track spacer (session 4 entry above).

### 2026-06-21 — Live I/O robot position column
- **Robot (KUKA)** panel: three columns **Position | Inputs | Outputs** (`Lfam3LiveIoPanelView.axaml`, styles in `BaseStyles.axaml`).
- **Position column:** JOINTS (A1–A6, E1), TCP mm (X/Y/Z), ABC ° — live when C3Bridge synced; copies from `RobotPanelViewModel` on `$AXIS_ACT` / `$POS_ACT` updates (`LiveIoMonitorViewModel.UpdateRobotPoseSection`).
- Extruder section unchanged (dual Inputs | Outputs).

### 2026-06-21 — LFAM 3 geometry regression (corrupt `lfam3.json`)
- **Symptom:** After joint-limit sync, LFAM 3 robot/beds/stands/tools vanished at runtime.
- **Cause:** Repo `assets/cells/LFAM3/lfam3.json` was stubbed (`joints: []`, `stands: []`, `tools: []`, `modelPath: "robot.glb"`). `CellPaths` prefers NAS repo over AppData, so app loaded the empty config.
- **Fix:** Restored repo copy from `%LOCALAPPDATA%\MassiveSlicer\assets\cells\LFAM3\lfam3.json` (dev-tuned stand poses + updated joint limits). Documented limits/poses in **LFAM 3 KUKA joint limits & cell poses** (this file).

### 2026-06-21 — LFAM 3 joint limits from KUKA `$machine.dat`
- Read soft limits from `\\192.168.0.153\krc\ROBOTER\KRC\R1\Mada\$machine.dat`.
- Corrected A1 (was ±60 → ±185) and A2 max (was +70 → **−5**).
- Updated `lfam3.json` in AppData + `src/MassiveSlicer.App/Assets` (repo `assets/cells` was corrupted separately — see above).

### 2026-06-21 — Pick/Deposit sim + selection-driven sidebar
- **Tool-change simulation:** `ToolChangeSequence.cs`, `KrlToolChangeSequenceParser.cs`, `ToolChangeSequencePathBuilder.cs`, `SequencePathRenderer.cs`, `ViewportView.ToolChangeSequence.cs`; buttons in `Lfam3WorkflowTimelineView.axaml`; MassiveCONNECT parity (KRL parse, LIN/PTP path, yellow marker, mount gating).
- **Sidebar sync:** `SyncRightPanelToViewportSelection` — source mesh → ADDITIVE; toolpath → TOOLPATH; LFAM 3 phase fallback when Additive tab hidden.
- **Polish:** Pick/Deposit chevron icons, white border when sequence active; toolpath deselected on sim start; sequence cleared on cell swap.

### 2026-06-21 — Milestone: LFAM 3 Live I/O Phases 1–3 (GitHub)
- **Committed & pushed** @ `dae3b33` on `feature/scan-rotary-bed-calibration`: full Live I/O stack — `Lfam3LiveIoCatalog`, `ExtruderBridgeClient`, `MillingModbusClient`, `LiveIoMonitorViewModel`, `Lfam3LiveIoPanelView`, workflow host, snapshot tests.
- **Phases 1–3** marked complete in `LiveIoPhasePlan`; roadmap: `P1 live · P2 live · P3 live`.
- **Documented** signal map + spindle-RPM limitation in **LFAM 3 Live I/O map** (this file).
- **Field:** milling bridge live on `192.168.0.249:8765` (8/8 `MILLING_IO` keys).

### 2026-06-21 — Milling bridge live on 192.168.0.249
- **Deployed** `lfam-monitor.service` on milling RevPi `192.168.0.249` (`pi`) — bridge ping + 8/8 `MILLING_IO` keys OK (yellow lamp ON at deploy time).
- **Corrected IP** from stale `192.168.0.246` → `192.168.0.249` in `lfam3.json`, deploy script default, tests, `LiveIoPhasePlan`.
- Redeploy: `python scripts/deploy_bridge_lfam3_milling.py --pass …` (requires `pip install paramiko`).

### 2026-06-21 — Milling bridge deploy script on GitHub
- **`scripts/deploy_bridge_lfam3_milling.py`** committed to [MassiveSlicer](https://github.com/MattWhite3194/MassiveSlicer) — SSH deployer for LFAM 3 milling RevPi (`192.168.0.249`): uploads `lfam_monitor_bridge.py`, installs `lfam-monitor.service` on :8765, verifies ping + `MILLING_IO` read.
- Canonical copy: repo `scripts/` — on GitHub `main` @ `c96efb8` (also `feature/scan-rotary-bed-calibration` @ `bd06307`). Network `Install/` folder is an optional mirror.

### 2026-06-21 — Live I/O Phase 3: Milling Spindle
- **`MillingModbusClient`:** polls milling RevPi lfam-monitor bridge (`millIp:8765`, 3 s) — `MILLING_IO` names in `io` dict (matches `modbus_monitor.py`, not Modbus TCP :502).
- **`CellConfig` / `lfam3.json`:** `millIp`, `millBridgePort`, `hasMilling`.
- **`LiveIoMonitorViewModel`:** milling poll loop + `SetMillingBridgeConfig` wired on cell swap.
- **`LiveIoPhasePlan`:** Phase 3 **Implemented**; tests in `MillingBridgeSnapshotTest.cs`.

### 2026-06-21 — Live I/O Phase 2: Pellet Extruder
- **`ExtruderBridgeSnapshot`:** parses bridge `modbus` dict + `modbus_connected` / `modbus_error` (mirrors `modbus_monitor.py` `_poll_extruder`).
- **`ApplyExtruderSnapshot`:** maps flat `io` → `ExtruderBridge` + `ExtruderIo28`; `modbus` regs → `ExtruderModbus` when connected.
- **`LiveIoValueFormatter`:** Modbus temps = raw °C; bridge RTD ÷10; MIO raw ÷1000 V.
- **`Lfam3LiveIoCatalog`:** zone regs aligned to HMI map (`hr_30201`/`hr_30101` … Zone 3, `hr_30200` gearbox).
- **`LiveIoPhasePlan`:** Phase 2 marked **Implemented**; tests in `ExtruderBridgeSnapshotTest.cs`.

### 2026-06-21 — Consolidate docs: `memory.md` only
- Merged evergreen content from `CLAUDE.md` into this file (stack, structure, UI, MVVM, coordinates, glossary).
- Deleted `CLAUDE.md` — no AI-specific memory files; `memory.md` is canonical on GitHub.

### 2026-06-21 — Workflow progress line icon-to-icon
- Green connector segments now `ColumnSpan=2` with inner grid `0.5*,*,0.5*` so each line runs icon-center to icon-center (was single-column, stopped halfway).

### 2026-06-21 — Live I/O chevrons + panel height
- **Chevrons:** Live I/O toggle — collapsed ▲ (show), expanded ▼ (hide). Workflow minimize unchanged (already correct).
- **Height:** Live I/O scroll `400px`; workflow overlay grows to `560px` when I/O expanded (was `220` / `320` caps).

### 2026-06-21 — memory: always show full build + run command
- Canonical block is the explicit `Stop-Process` → `dotnet publish` → `Start-Process` script above.
- Agent rule: after code changes, paste the **full** block for the user (not abbreviated).

### 2026-06-21 — N-tab regression removed (again)
- **Symptom:** Always-visible “N” floating div on left viewport edge returned after overlay revert.
- **Fix:** Removed N-tab `Border`; HUD uses `IsVisible="{Binding IsSyncHudOpen}"` (no `SyncHudTranslateX` slide); `ResetViewportOverlayState()` closes HUD on boot/cell swap.
- **Rule:** See “Viewport N-key HUD — do not regress” above — never re-add the edge tab.

### 2026-06-21 — Single publish folder + N-menu VIEW/DEV
- **One folder only:** `%LOCALAPPDATA%\MassiveSlicer\build` — `build2`/`build3`/`build4` retired.
- **Script:** `scripts/publish-and-run.ps1` (canonical build + launch).
- **Cell data:** repo `assets/cells` already matches newest copies; dev saves go to repo via `CellPaths`.
- **N menu:** VIEW (Save View) + DEV back inside slide HUD; removed duplicate bottom-left Save View.

### 2026-06-21 — Root cause: failed bindings default visible (not “force close”)
- **Symptom:** All overlay panels + right-panel expanders appear open on boot.
- **Root causes (3):**
  1. `OverlayView` had no `DataContext` binding — relied on one-shot `WireGlCanvas` assignment; when DC lagged, every `IsVisible="{Binding …}"` failed silently → Avalonia default `IsVisible=true`.
  2. `SectionExpander` template used `{TemplateBinding IsExpanded}` on `ContentPresenter` — collapse did not hide content in Avalonia 12; fix is `{Binding #PART_toggle.IsChecked}`.
  3. `NotifyCellChanged()` forced `IsLfam3WorkflowExpanded = true` on LFAM 3 load — full workflow bar opened by code, not user action.
- **Fixes:**
  - `ViewportView.axaml`: `OverlayView DataContext="{Binding}"` + compiled bindings/`x:DataType`.
  - `ViewportOverlayView.axaml` + `Lfam3WorkflowTimelineView.axaml`: `x:CompileBindings="True"` + `x:DataType="vm:ViewportViewModel"`.
  - `BaseStyles.axaml`: SectionExpander content visibility → `#PART_toggle.IsChecked`.
  - `NotifyCellChanged`: stop auto-expanding LFAM 3 workflow; collapsed chip on boot.
  - `Lfam3WorkflowTimelineView`: Live I/O scroll uses `{Binding LiveIo.IsExpanded}` (removed code-behind visibility).
- Published: `%LOCALAPPDATA%\MassiveSlicer\build`

### 2026-06-21 — Viewport overlay visibility REVERT (binding-driven UI restored)
- **Mistake:** Prior session replaced working XAML `IsVisible` bindings with code-behind `ApplyOverlayVisibility` / `SetPanel`, defaulting all panels to `IsVisible=False`, and gating transform toolbar via `ShowTransformToolbar`. Broke N HUD, LFAM3 timeline, focus bar, transform toolbar.
- **Fix:** Restored HEAD pattern — overlay visibility in XAML only; `Key.N` in `ViewportView.OnKeyDown`; transform toolbar always visible; focus/mesh buttons use `HasSelection` + `!IsToolpathSelected`. (**Note:** a later session wrongly re-added the always-visible N tab + slide transform — fixed above.)
- **Removed:** `ViewportOverlayView.Bind/Refresh`, `MainWindow` N KeyBinding + `Refresh()` fan-out, `ShowTransformToolbar`, HUD close on cell swap.
- **Kept:** LFAM3 workflow (binding `ShowLfam3WorkflowTimeline` / `ShowLfam3WorkflowCollapsedBar`), dev mode in slide HUD, seam editor, merge toolpaths, mesh cleanup, transparent overlay background.
- Published: `%LOCALAPPDATA%\MassiveSlicer\build`

### 2026-06-21 — Viewport visibility + overlay fix
- Transparent viewport overlay; expander collapse fix; workflow UI height limits; right panel defaults collapsed.

### 2026-06-21 — Boot crash fix
- `ApplyCellSwap` GL-thread `DataContext` access removed.

### 2026-06-21 — `affecto_staubli.glb` + asset cache
- JSON glTF sidecar embedding in `AssetLocalCache`; all LFAM3 load tests pass.

### 2026-06-21 — Cell/robot visibility + threading
- UI-thread marshaling for cell swap and console; viewport GL init improvements.

### Earlier (same sprint)
- Console command system + autocomplete.
- LFAM 3 workflow timeline refactor + Live I/O panel.
- Bottom dock full-width + resizable console.
- GLB import test path + meshopt decode.
- Dev transforms for stands / rotary bed / docks.

---

---

## Build log (status-bar build numbers)

> **Build numbering (2026-07-16, build 416+):** the build number is now generated
> automatically at compile time as the **git commit count**, shown in the status bar
> as `build N · date · sha`. It is identical on every machine, increments with every
> commit, and maps 1:1 to a commit — `git log --oneline` connects any build number
> to its changes. Builds 1–30 below were hand-numbered before this scheme.

Build numbers appear in the status bar (bottom center). Numbering began mid-development
during the Jefre curtain project debugging; builds 1–8 are reconstructed from the
session that introduced them.

## Builds 1–8 — macOS crash hunt (Sine wave + Show bead)
- Added `SliceLogger`: timestamped slice-pipeline diagnostics written to
  `~/Desktop/massiveslicer-slice.log`, with per-stage timings.
- Diagnosed and fixed GPU memory crash: bead rendering allocated ~390 MB for
  wave-expanded toolpaths (500k+ moves × 36 verts); introduced bead decimation
  (MaxBeadSegments) to cap the upload.
- Build 8: wrapped the GL upload path in a logging try-catch — unhandled managed
  exceptions had been escaping to the native render loop and aborting the process
  (SIGABRT); now they log the real exception and show an error instead.

## Build 9 — bead crash root cause
- Fixed `IndexOutOfRangeException` in bead upload: decimation counted selected
  moves globally but the selection loop reset per layer, so the vertex array was
  undersized by one bead per layer.

## Builds 10–14 — bead rendering iterations
- 10: merged decimated moves into continuous beads (no gaps between segments).
- 11–12: experimented with rendering beads from the raw (pre-wave) toolpath;
  reverted — bead must show the wave geometry.
- 13: bead fallback color white instead of hardcoded blue.
- 14: beads render the wave toolpath; segment budget raised to 160k.

## Build 15 — the blue color mystery, solved
- Toolpath/bead color was coming from three overriding sources: saved
  `prefs.json` (stored blue from an old session), the material preset color
  mapping (fallback blue), and hardcoded defaults. All defaults now white;
  material fallback white.

## Build 16 — live bead color
- "Bead color" picker next to Show Bead: applied per-frame as a shader uniform,
  so it recolors already-sliced beads instantly (no re-slice), and persists.

## Builds 17–18 — wave phase lock experiments
- Attempted seam-drift fixes for the sine wave (fixed world anchor, then
  direction-aware chained anchor). Measurement showed no improvement — these
  led to the correct diagnosis and the Fixed/Dynamic design in build 21.
- Added `tools/wave_analysis.py`: measures wave phase coherence in exported
  .src files (direction-aware probes, stagger-subtracted residuals).

## Build 19 — high-fidelity bead renderer
- Rebuilt bead mesh as indexed geometry with chord-error decimation (points
  dropped only where the path is straight within 0.35 mm). Full 2.7M-move wave
  toolpaths now render faithfully — the old fixed-step decimation cut chords
  across entire wavelengths and misrepresented the wave.
- Overhang/orientation overlays share the bead index buffer (also fixed a
  latent ~2.3 GB allocation on wave toolpaths).

## Build 20 — scrub repaint fix
- Viewport froze when scrubbing through robot-unreachable poses (repaint only
  fired on successful IK solves). Scrubbing now always repaints.

## Builds 21–22 — Fixed / Dynamic wave phase methods
- New "Phase" dropdown in wave settings:
  - **Fixed** — original seam-anchored behaviour, byte-identical output
    (verified against the printed curtain program).
  - **Dynamic** — phase inheritance: each layer continues the wave of the layer
    directly below plus stagger, eliminating the layer-to-layer drift that
    produced moiré texture bands ("resonance") on morphing cross-sections.
- 22: robust Dynamic fitting (median filter, symmetric slope clamp) after
  measurement showed one-directional lag in deep folds.

## Builds 23–24 — saving, progress, and load
- Fixed doubled extensions from save dialogs (`.src.src`, `.mass.mass`) across
  all five save flows (workspace, KRL, Send to Robot, PLY, STL).
- Workspace loading moved off the UI thread (no more 20–30 s freeze).
- Real 0–100 % progress with stage text: byte-accurate file-read progress on
  project open; per-layer progress during planar slicing.
- macOS: `MassiveSlicer.app` bundle generator (`tools/make_macos_app.sh`) and
  runtime Dock icon, so Cmd+Tab shows the Massive logo and app name.

## Build 25 — material flow calibration (purge & weigh)
- Material Preset dialog gains a guided CALIBRATION section: run the extruder
  at a known motor % for a known time, weigh the purge, enter three numbers —
  the per-material flow rate (rev/cm³) is computed and applied with provenance
  (date + conditions). Fixes wrong starting RPM from the uncalibrated default
  constant (the flow creep that smeared the bottom of the curtain print).

## Builds 26–28 — singularity defense (curtain print failure)
- 26: loud robot-validation summary after every slice (singularity/unreachable
  counts + height range) and a confirmation gate on Export KRL / Send to Robot.
- 27: **TCP auto-rotation repair** — for flagged spans, searches the smallest
  print-neutral nozzle spin that steers the wrist clear of singularity, ramps
  it in/out smoothly, re-verifies with IK, and bakes per-move yaw into the KRL
  export. Also fixed the exporter's gimbal-lock handling which silently zeroed
  any TCP rotation for a straight-down tool — the root cause of "the slicer
  never rotates the TCP".
- 28: "Go to first issue" jumps the timeline to the first flagged move so the
  robot preview shows the failing pose.

## Builds 29–30 — inline status UI (no popups)
- Progress and alerts moved into the bottom status bar: busy strip with a
  determinate progress bar + stage text, and a red ⚠ validation alert row with
  the Go-to-issue button. Export KRL keeps its confirmation dialog, now with
  Cancel / Go to issue / Export anyway.
- Status bar shows the loaded workspace filename instead of "No file loaded".
- 30: repeatable wording in the export warning; timeline markers preserved
  after Go-to-issue.

