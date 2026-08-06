# MassiveSLICER V3 — Project Memory

## ⚠️ Active work: `feature/spsm` (Scan–Print–Scan–Mill) — NOT fully merged to main

**Branch:** `feature/spsm` (tracking `origin/feature/spsm`). First push: `8568268` (default cell prefs + Scan/Mill StepCard chrome). **Large pile of uncommitted SPSM work** still local (mill bits library, STEP via cascadio, mill area soft-paint, phase-switch fix, MCP mill commands) — commit when Jeff wants.

**This PC (MassiveMAKE shop workstation):** machine-local defaults only (do not commit personal prefs):
- Default cell: **LFAM 3** (`AppPreferences` / `DefaultCellName`)
- Windows GPU High Performance for `MassiveSlicer.App.exe` when set via registry earlier
- Mill tool library: `%LOCALAPPDATA%\MassiveSlicer\mill_tools.json` (v3 schema)
- STEP converter venv: `%APPDATA%\MassiveSlicer\step-env` (`numpy` + `cascadio`)

Last updated: **2026-08-06** (Seam guides work on a sliced part; auto-slice on import restored; per-head material calibration + calibration flow-offset leak fixed; panel ADVANCED sections)

---

## Older note: Cut Modifier (historical)

Cut Modifier lived on `feature/cut-modifier` (non-destructive split). Treat production trust as shop-validated only. See archive if needed.

> **Single source of truth** for humans and all AI assistants working in this repo. Session progress, architecture, conventions, and commands live here — **not** in tool-specific files. (`CLAUDE.md`/`AGENTS.md` exist only as thin auto-loaded pointers that route assistants here and to `ROADMAP.md`, and carry the doc-maintenance rules.)

> **Living doc.** Update after bug fixes, features, test results, and priority shifts so every session starts with shared context.

## How we keep this file current

**When to append**
- After fixing a bug (symptom → cause → fix → files touched)
- After shipping or merging a feature
- After tests pass/fail on something important
- When the user reprioritizes (move items between Pending ↔ Completed)
- Before pausing work for the day

**What to add**
- Dated changelog entry at the bottom (newest first)
- Bump `Last updated` date at the top
- Update **Pending** when something is done or deferred
- Add **Key files** rows when a new subsystem appears
- Keep **Expected console output** / test table accurate

**What to avoid**
- Duplicating full code — point to paths and one-line behavior instead
- Letting this file grow past ~800 lines. Every session skims it, so length is a real
  recurring cost. When it gets long, move the oldest changelog entries verbatim to
  `docs/memory-archive.md` and leave a pointer (done 2026-07-27: pre-07-04 entries + build log).
- Stale “in progress” items — mark done or move back to Pending

**Agent rule:** At natural stopping points, offer to update `memory.md` or update it without being asked if the session included substantive code changes.

**Agent rule (build/run):** After any code change, always give the user the **full** canonical build + run block below — not a shortened “publish to build” line, not script-only, not start-only unless they asked for start-only.

---

## Project overview

MassiveSlicer is a C# desktop rewrite of a KUKA robot CAM app (original Electron/JS prototype: `MassiveSlice`). It generates additive/subtractive toolpaths for KUKA KRC4 robots, exports KRL, previews motion, and connects live to the controller via C3Bridge.

The prototype UI layout and workflows are the reference. The 3D stack was replaced because Three.js is Y-up while KUKA/CAM in this project use **Z-up right-hand** coordinates.

### Stack

| Layer | Technology |
|-------|------------|
| UI | **Avalonia** (.NET 8), XAML, MVVM |
| 3D viewport | OpenTK (OpenGL) via `GlHostControl` |
| Coordinates | **Z-up, right-hand** — enforced at rendering |
| Robot comms | C3Bridge TCP to KRC4 (port 7000) |
| IK/FK | Custom C# solver (KR120) |
| Tests | xUnit (`MassiveSlicer.Tests`) |

### Solution structure

```
src/
├── MassiveSlicer.App/       # Avalonia shell, Views, ViewModels, cell load, console
├── MassiveSlicer.Core/      # Models, slicing, kinematics, IO, C3Bridge (no UI)
├── MassiveSlicer.Viewport/  # OpenGL scene, loaders, renderers, camera
└── MassiveSlicer.Tests/     # xUnit tests
```

**Dependency rule:** `Core` has no UI deps. `Viewport` depends on `Core`. `App` depends on both.

### UI layout (reference)

| Region | Description |
|--------|-------------|
| **Top toolbar** | File menu, camera presets, console/settings toggles |
| **Left panel** (~220px) | Cell selector, OUTLINER/ASSETS, scene tree |
| **Center viewport** | OpenGL canvas, overlays, transform toolbar, LFAM 3 workflow |
| **Right panel** (300–400px) | ADDITIVE / SCAN / SUBTRACTIVE / SETTINGS |
| **Bottom dock** | Resizable console + 24px status footer |

Right panel SETTINGS: VIEW (themes, lights), UV (stub), ROBOT (joints, TCP, sync), PROPS.

### MVVM

`MainWindowViewModel` owns panel VMs; views bind to VMs only. Key children: `ViewportViewModel`, `LeftPanelViewModel`, `RightPanelViewModel` (Additive/Scan/Subtractive/Settings), `ToolbarViewModel`, `ConsoleViewModel`, `LiveIoMonitorViewModel`.

### Coordinate system

Z-up right-hand everywhere: X forward, Y left, Z up. OpenGL camera uses Z-up from the start — no global Y→Z hack. KUKA ABC (Euler ZYX) maps directly.

### Domain glossary

- **KRL** — KUKA Robot Language (`.src` move programs)
- **C3Bridge** — TCP protocol for live joint I/O from KRC4
- **TCP** — Tool Center Point (not networking)
- **BASE / TOOL_DATA** — KUKA frame indices
- **D-H** — Denavit-Hartenberg FK/IK for KR120
- **Slicing** — Planar, angled, geodesic/surface modes

### KUKA controller GOTCHA — R1 program recognition needs a controller restart

Any KRL program **created, edited, or deleted in the controller's `R1\Program` folder** (e.g. deploying `BED_SCAN_CAL.src` / `SCAN_TOOL_CAL.src` to `\\<bridgeIp>\krc\ROBOTER\KRC\R1\Program\` over SMB) is **NOT recognized by the KRC until the KUKA is restarted** (control-PC reboot / KSS restart). Until then the file is on disk but the Navigator/selection won't see the new/changed version — so a freshly deployed program won't appear or run on the pendant. This (plus C3 remote-select being unavailable here — KUKAVARPROXY only, no C3 Bridge Interface Server → `Select … E_FAIL`) is why an auto-deployed calibration program "doesn't load to the HMI." Workflow: deploy → **restart the KUKA** → then on the pendant Navigator → `R1\Program` → Select the program → Start. Related: [[relief-milling]], rotary/scan calibration handshake.

### Not ported from JS prototype

Three.js rendering, `kinematics.js` IK, `c3bridge.js`, and `main.js` global state — all rewritten in C# with MVVM + proper Z-up.

### Other dev commands

```powershell
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~CellSceneLoadTest"
dotnet format
```

---

## Project locations

| What | Path |
|------|------|
| Repo (this machine / MassiveMAKE PC) | `C:\Users\MassiveMAKE\MassiveSLICER` |
| Repo (NAS historical) | `\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER V2\` |
| GitHub (canonical) | https://github.com/massivemake/MassiveSLICER |
| Active branch | **`feature/spsm`** |
| Publish (optional) | `%LOCALAPPDATA%\MassiveSlicer\build` |
| Dev run (this PC often) | `src\MassiveSlicer.App\bin\Release\net8.0-windows\MassiveSlicer.App.exe` |
| Cell JSON (canonical) | Repo `assets\cells\` |
| Mill bit library (user) | `%LOCALAPPDATA%\MassiveSlicer\mill_tools.json` |
| STEP env (cascadio) | `%APPDATA%\MassiveSlicer\step-env` |
| Bridge port file | `%LOCALAPPDATA%\MassiveSlicer\bridge.port` |
| Test GLB | `assets\test\crystal_stone_rock.glb` |

### Build + run (canonical — always paste this in full)

```powershell
Stop-Process -Name "MassiveSlicer.App" -Force -ErrorAction SilentlyContinue
Set-Location 'C:\Users\MassiveMAKE\MassiveSLICER'
dotnet build 'src/MassiveSlicer.App/MassiveSlicer.App.csproj' -c Release
if ($LASTEXITCODE -eq 0) {
    Start-Process -FilePath '.\src\MassiveSlicer.App\bin\Release\net8.0-windows\MassiveSlicer.App.exe' `
      -WorkingDirectory '.\src\MassiveSlicer.App\bin\Release\net8.0-windows'
}
```

Publish variant (optional): `scripts\publish-and-run.ps1` → `%LOCALAPPDATA%\MassiveSlicer\build`.

### Start only (no rebuild)

```powershell
Start-Process -FilePath 'C:\Users\MassiveMAKE\MassiveSLICER\src\MassiveSlicer.App\bin\Release\net8.0-windows\MassiveSlicer.App.exe' `
  -WorkingDirectory 'C:\Users\MassiveMAKE\MassiveSLICER\src\MassiveSlicer.App\bin\Release\net8.0-windows'
```

---

## Completed features

### SPSM / Mill sidebar (`feature/spsm`, 2026-07-31)

**Goal:** Scan–Print–Scan–Mill workflow on LFAM 3 — Mill panel structure, bit library, area paint, reliable STEP import.

#### Mill right-panel structure (LFAM 3)
- **1 BITS** — spindle tool library dropdown + dialog; default **Flat 3in AP90** (`MillBitTool.CreateLfam3DefaultFlat3In`); library JSON v3 under AppData.
- **2 OPERATION** — strategy tiles (`MillOperationKind`: MultiAxisFinishing, Drilling, PlanarFacing, PlanarClearing, Cutout, Contouring, Swarf) + **SELECT AREA**.
- **3 TOOLPATHING** — passes / travel / movement; SpindleRpm linked between BITS and TOOLPATHING.
- **MORE** — catch-all.
- Scan/Mill StepCards match Printing styling; BACK TO STEPS removed.
- Key UI: `RightPanelView.axaml`, `SubtractiveSettingsViewModel.cs`, `MillBitLibraryDialog.axaml`, `MillBitLibraryViewModel.cs`, `MillBitTool.cs`, `MillBitLibraryLoader.cs`, `MillOperationKind.cs`.

#### SELECT AREA (soft brush on workpiece only)
- Tools: Whole / Face / Box / Lasso / Brush / Clear.
- **Workpiece-only:** `ViewportViewModel.IsMillableWorkpiece` — user imports/scans; never robot, bed, cell env, toolpaths, effectors, modifiers.
- **Not UV-atlas dependent:** `MillSurfacePaint` stores **world-space vertex weights** (soft falloff). Weights upload into dedicated paint vertex channel (`aPaintUv.x`); material TEXCOORD_0 and PBR maps (units 4–8) untouched.
- Shader: `MeshRenderer.applySelectionOverlay` — **lime green** wash (`SelectionTint` ~`0.25,1.0,0.20`, strength ~0.88). Applied in Standard / fastcell / arctic / layer / wire / normals paths.
- Brush UI: bottom-center toolbar when Brush armed (`ShowMillBrushToolbar`, ~250px from bottom) — Size mm + Falloff. **No right-click menu, no sphere cursor.**
- Alt = erase. Hit triangle always flooded so a pick always leaves a mark.
- Key: `MillSurfacePaint.cs`, `ViewportView` mill handlers, `ViewportOverlayView.axaml` toolbar, `Picker.FaceHit` / `PickFaceDetailed`.

#### STEP import (Windows)
- **Occt.NET removed from the load path** — TianTeng package popped a garbled license MessageBox and crashed the host.
- **Cascadio** (Python OCCT wheel) via private venv: `CascadioStepConverter.cs` + thin `StepLoader.cs` (all platforms). Needs Python 3 on PATH first run.
- Non-Windows already used cascadio; now shared.
- Legacy files kept but not compiled: `OcctBootstrap.cs`, `OcctUiSuppressor.cs` (MessageBox auto-OK experiment — superseded).
- Package refs stripped from App/Viewport/Tests csproj.

#### GLSL / launch crash gotcha (reconfirmed 2026-07-31)
- NVIDIA rejects **any non-ASCII** in shader source strings (even comments) → `error C0000: unexpected $end` → app dies on first mesh upload.
- Keep `MeshRenderer.VertSrc` / `FragSrc` pure ASCII.

#### LFAM 3 phase switch: do not select TCP
- `SelectLfam3WorkflowPhase` updates `_lfam3WorkflowPhaseIndex` + sidebar only.
- **Does not** call `SelectLfam3Tool` (that set `Robot.SelectedToolIndex` → mount → `_renderer.Select(tool)`).
- Tool mount still via explicit pick/deposit or robot tool dropdown.

#### Console + MCP milling
- Console: `mill status | mill area <whole|face|box|lasso|brush|clear> | mill brush size <mm> | mill brush falloff <0-1> | mill op <Kind>`
- MCP: `massiveslicer_mill` tool + `massiveslicer_command` docs in `scripts/mcp/massiveslicer_mcp.py`.
- Bridge: `POST /command` on port from `bridge.port`.

#### Machine-local (do not commit)
- Default cell LFAM 3; GPU High Performance preference; mill_tools.json; step-env venv.

### PBR rendering (metallic-roughness + material inspector)
- Imported GLBs render real **metallic-roughness PBR** from their textures (base colour, MR, normal, AO, emissive) via Cook-Torrance + env IBL + ACES tonemap. Data model: `TextureData`/`MaterialData` + `MeshData.Uvs/Tangents/Material`; loader decodes/dedups images (StbImageSharp); GPU textures pooled in `GpuTextureCache` (units 4-8). Single uber-shader in `MeshRenderer.FragSrc` (mode 0 = PBR; modes 1/2/3 = normals/layer/fastcell unchanged; presets via factor path).
- **Material debug channels** (`ShaderMode` BaseColor/Metalness/Roughness/NormalMap/AO/Emission/UvChecker/**Wireframe** → shader modes 4-11), picked from the **MATERIAL CHANNELS** section in `LeftPanelView` (Viewport tab) **and** the **Material ▾** dropdown in the viewport's top-left toolbar (`ViewportOverlayView.axaml`). **Wireframe** = flat (faceted) shading from `dFdx/dFdy` face normals + a second `PolygonMode.Line` pass (`uWireframe` uniform), for inspecting topology.
- **GLSL GOTCHA:** the NVIDIA GLSL compiler rejects **non-ASCII** characters anywhere in shader source (even comments) with a misleading `error C0000: syntax error, unexpected $end at <EOF>`. An em-dash `—` in a shader comment crashed the app on launch (shader compile throws in `MeshRenderer` ctor). Keep `MeshRenderer.VertSrc`/`FragSrc` strictly ASCII — use `--`, not `—`. (C# comments outside the shader strings are fine.) Full details + the NAS stale-build gotcha in the session-6 changelog.
- **Viewport toolbar (top-left, `ViewportOverlayView.axaml`)** grouped with vertical dividers: `[Lay on Face · Drop to Plate]` ⎮ `[Focus · Ungroup · Explode · Mesh Cleanup]` ⎮ `[Material ▾]`. The **Material ▾** flyout = channel view selector (Final Render + the 7 channels, active-highlight via `EnumMatchCvt`) + Exposure & Reflections sliders. Gizmo Move/Rotate/Scale stays a separate top-center toolbar. Note: Avalonia Flyout popups aren't reachable via UIA (separate tree) — verify flyout bindings by screenshot, not automation.

### LFAM 3 workflow timeline UI
- **Architecture (3- and 4-phase tracks):** `Lfam3WorkflowPhaseBlock` = rivet + phase label only. `Lfam3WorkflowPhaseColumn` wraps block + float layer (`Lfam3WorkflowPickDepositFloat`, playback, param card). Both grids in `Lfam3WorkflowTimelineView.axaml` use `Lfam3WorkflowPhaseColumn` — `Lfam3WorkflowPickDepositOverlay` removed.
- **Rivet alignment (user-verified):** track grid `RowDefinitions="56"`, `Lfam3WorkflowTrack` height **68px**, phase column `Height="56"` + `VerticalAlignment="Center"`, connector `VerticalAlignment="Center"` through 52px icon centers. `ClipToBounds="False"` on track/panel/column.
- Larger phase icons in `BaseStyles.axaml` (76×76 host, 54px node, 32px icons).
- Cleaner phase borders and column structure; chevron/stem/connected layout removed.
- Live I/O toggle above workflow phases; expands into `Lfam3LiveIoPanelView`.
- **Live I/O Phase 2 (Pellet Extruder):** `ExtruderBridgeClient` polls `extIp:8765` every 2 s — flat `io` (Pos30 DI/DO, Pos28 `O_*`, MIO/RTD analog) + `modbus` holding regs (`hr_301xx`/`hr_302xx` zone temps). Writable DO with confirm on bridge pins. Status: `P2 live · bridge + Modbus`.
- **Live I/O Phase 3 (Milling Spindle):** `MillingModbusClient` polls `millIp:8765` every 3 s — `MILLING_IO` RevPi DIO names in bridge `io` dict. LFAM 3: `millIp` **192.168.0.249** (RevPi130866), `hasMilling: true`. Bridge deployed (`lfam-monitor.service` active). Status: `P3 live · bridge`.
- **Live I/O milestone (Phases 1–3):** `LiveIoPhasePlan` all **Implemented**; catalog in `Lfam3LiveIoCatalog.cs`; panel in `Lfam3LiveIoPanelView`; poll loops in `LiveIoMonitorViewModel`. See **LFAM 3 Live I/O map** below.
- LFAM 3 sidebar tab gating: Print → Additive, Scan → Scan, Mill → Subtractive (`SyncLfam3WorkflowSidebar`).
- **LFAM 3 Pick/Deposit simulation:** KRL parser (`KrlToolChangeSequenceParser`), path overlay (`SequencePathRenderer`), Pick/Deposit buttons in workflow phase cards; 8 s playback, tool dock/flange swap at `USRTOOLTYPE` waypoint; active button highlight + chevron icons; deselects toolpath on start.
- **Pick/Deposit placement:** Visible only on **active** phase (`IsStepActive`). Float lives in a **`Canvas`** in `Lfam3WorkflowPhaseColumn.axaml` (`Canvas.Bottom="78"` pins the float's bottom above the rivet). Track reserves 90px paint room (spacer + `Margin="0,-90,0,0"`). Click Pick/Deposit → `ActiveToolChangeSequenceId` → `ToolPanel.ShowPlayback` expands playback strip + param card. Stack order bottom→top is **pills → param card → playback**, so it grows strictly **upward** (independent of `LiveIo.IsExpanded`). See **LFAM 3 workflow layout — do not regress** below.
- **Closing the menu clears the viewport toolpath:** the playback collapse chevron (`CollapseToolChangePlaybackCommand` → `CollapseToolChangePlayback` in `ViewportView.ToolChangeSequence.cs`) calls `ClearToolChangeSequence()` — hides the playback strip **and** removes the tool-change path overlay/markers, restores the prior mounted tool, and deactivates the pills.
- **Connector line ends at the rivet perimeter:** each rivet (`Lfam3WorkflowPhaseBlock`) has an opaque 54px disc (`#0A0E14`, track bg) behind the node ellipse so the green/grey connector line is masked behind the circle instead of showing through the semi-transparent node fill.
- **Live I/O robot position column:** Robot panel uses **Position | Inputs | Outputs** (`Lfam3LiveIoPanelView.axaml`, `LiveIoMonitorViewModel.cs`). When synced: A1–A6 + E1 from `$AXIS_ACT`, TCP X/Y/Z + A/B/C from `$POS_ACT` via existing `RobotSyncService` stream (~10 Hz). Extruder panel stays Inputs | Outputs only.
- **Viewport selection → right panel:** mesh click → ADDITIVE tab; toolpath click → TOOLPATH tab (`SyncRightPanelToViewportSelection` in `MainWindowViewModel`).

### LFAM 3 Live I/O map (Phases 1–3 complete)

**Cell endpoints** (`lfam3.json`):

| Subsystem | IP | Port | Protocol |
|-----------|-----|------|----------|
| KUKA C3Bridge | `bridgeIp` (cell) | 7000 | TCP JSON vars |
| Pellet extruder RevPi | `extIp` **192.168.0.196** | 8765 | lfam-monitor JSON bridge |
| Milling cabinet RevPi | `millIp` **192.168.0.249** | 8765 | lfam-monitor JSON bridge |

**Phase 1 — Robot (KUKA)** — `LiveIoSource.Kuka`, ~2×/s via C3Bridge when synced:
- **Position readout** (sync only, separate from I/O poll): `$AXIS_ACT` → A1–A6 + E1 °; `$POS_ACT` → TCP X/Y/Z mm + A/B/C °. Wired `RobotPanelViewModel` → `LiveIoSectionViewModel` pose props; shown in left **POSITION** column.
- Digital IN: `$IN[6,7,10–15,17]` (extruder ready, flange, tool changer, pressure)
- Digital OUT (writable + confirm): `$OUT[5,7,9,11–16]`
- Analog OUT (display): `$ANOUT[1–4]` — zones 1–3 °C, **extruder** RPM % (`KrlAnout` scaling)

**Phase 2 — Pellet Extruder** — `ExtruderBridgeClient` 2 s poll on `extIp:8765`:
- Pos30 DI/DO: safety gate, emergencies, contactors, lamps, motor enable, extruder-ready
- Pos28 valve DIO: `O_1` (lock), `O_5` (unlock) — writable
- Bridge analog: `AI_09_MIO_extruderMotorVel`, `AI_01_MIO_HLFB_motorVel`, `RTDValue_1/2`
- Modbus holding regs (when `modbus_connected`): `hr_30101–30103` setpoints, `hr_30200–30203` actuals (°C)
- Scanner bridge pins (Phase 2 shared): `DI_scanReady`, `DI_captureActive`

**Phase 3 — Milling Spindle** — `MillingModbusClient` 3 s poll on `millIp:8765`:
- DI: `DI_04_gateOpenStop`, `DI_05_SS1standstill`, `DI_06_SS1stop`, `DI_07_emergencyState`, `DI_08_digitalFromKUKA`
- DO (writable lamps): `DO_01_redLamp`, `DO_02_yellowLamp`, `DO_03_greenLamp`
- Deploy: `python scripts/deploy_bridge_lfam3_milling.py --pass …` → `lfam-monitor.service`

**Not on milling RevPi (confirmed):** spindle **RPM setpoint or actual speed**. Milling cabinet exposes **digital safety/status only**. Spindle speed is KUKA hardwired 0–10 V → Schneider ATV340 VFD (documented in `Install/CONTROLS_REFERENCE.md`); not in `MILLING_IO` or current bridge. `$ANOUT[4]` / `hr_30100` are **extruder** motor, not spindle. `DI_05_SS1standstill` = VFD at rest (bool), not RPM. Future: poll spindle `$ANOUT[n]` from KUKA or ATV340 Modbus.

### Bottom status / console dock
- Full-width bottom dock in `MainWindow.axaml` + `BottomLeftDockView.axaml`.
- Resizable console (drag grip); toolbar toggle.
- Status bar always visible (24px footer).

### KRL toolpath import (scrub, pick, bead, outliner)
- **`KrlToolpathParser`** parses inline Cartesian `LIN`/`PTP {X,Y,Z…}` frames from `.src` into a `Toolpath` (`LIN` → `MoveKind.Mill`, `PTP` → `MoveKind.Travel`). Joint-only programs (e.g. calibration sweeps) yield 0 moves.
- **`MainWindowViewModel.ImportKrlToolpath`** + console `import-krl` / file menu; positions offset by active cell robroot + bed base.
- **Outliner nesting:** imported KRL nodes appear under the active print object or **Rotary Bed** group (`ResolveToolpathParentOutlinerItem`, recursive `OutlinerItemView`).
- **Selection:** nested outliner clicks no longer snap back to rotary bed (`SuppressNextOutlinerListBoxSelection`); viewport pick hits **Mill + Travel** segments (`PickToolpath`, `ToolpathMoveKinds`).
- **Scrub / playback:** prefix sums treat Mill as cut geometry (not travel VBO); bead prefix array always sized even for mill-only paths (fixes scrub crash); `ScrubCount` bounds-checked.
- **Show Bead:** bead mesh + overhang/orientation overlays include **Mill** segments (same as extrude lines).
- **Shared helper:** `ToolpathMoveKinds.IsCutSegment` / `IsTravelSegment` in `ToolpathMove.cs`.
- **Tests:** `KrlToolpathParserTest`, `KrlImportOutlinerTest`, `KrlToolpathHandlingTest`.

### PBR material inspector + MCP bridge
- **`PbrMaterialSettings`:** per-map layer toggles, overlay strength, factor overrides (metal/rough/AO/emissive).
- **`MeshRenderer` / `SceneRenderer`:** per-layer `Use*Map` flags; `SyncPbrMaterial()`.
- **Local control bridge:** `GET|POST /materials` via `PbrMaterialBridge.cs`.
- **MCP:** `massiveslicer_materials_get` / `massiveslicer_materials_set` in `scripts/mcp/massiveslicer_mcp.py`.
- **Test:** `PbrMaterialSettingsTest`.

### Outliner + diagnostics polish
- **Delete hidden** for cell infrastructure (robot, rotary bed, stands, print bed) — `OutlinerItemViewModel.CanDelete`.
- **Nested outliner** via `OutlinerItemView.axaml` (recursive children).
- **Full-app screenshot:** `AppScreenshotCapture.cs` — `RenderTargetBitmap` on `MainWindow` (not viewport-only).
- **Tests:** `OutlinerCanDeleteTest`, `PickerTest`, rotary/scan outliner tests.

### Console commands
- `ConsoleCommandRegistry.cs`, `ConsoleViewModel.cs`, `ConsoleView.axaml`.
- Typed commands with Tab/↑↓ autocomplete and Enter to run.
- Commands: `help`, `clear`, `new`, `open`, `save`, `save-as`, `settings`, `panel-settings`, `import`, `import-krl`, `undo`, `redo`, `console`, `right-panel`, `frame`, **`slice`**, `prepare`, `preview`, **`reload-cell`**.
- **Simple slice flow:** `import <path>` (auto-selects the mesh) → `slice` (runs `Viewport.SliceCommand` on the selected mesh; no need to hunt for the ADDITIVE → Generate Slice button or use the file dialog). Clean printable test part: `assets/test/test_cube.stl` (300mm manifold cube, ASCII STL, Z-up mm). The slice is purely geometric — identical regardless of the material/view mode (PBR vs Wireframe).
- **Import vert-count log fix:** `ImportModelFromPath` now inspects the node **before** `AddUserNode` enqueues it. Previously the GL upload thread could clear `PendingMesh` before the inspector ran, logging "0 verts" for small meshes (a benign race, but it looked like a load failure).
- `import [path]` → `MainWindowViewModel.ImportModelFromPath` + `GltfImportInspector`.
- `reload-cell` → invalidates `CellSceneCache` and reloads active cell via `OnDevCellReloadRequested`.

### GLB import / diagnostics
- `GltfImportTest.cs`, `GltfImportInspector.cs`, `GltfLoader.cs` pipeline.
- Test asset: `assets/test/crystal_stone_rock.glb` (from Downloads).
- `GlbMeshoptDecoder` for EXT_meshopt_compression at load time.
- `GlbRepair` for glTF-Transform buffer byteLength mismatches.

### Cell dev transforms (LFAM 3)
- Stand + rotary table dev-mode adjustments restored and synced.
- `CellDevTransformSaver.cs`, Dev Mode toggle inside N-key HUD only (not a persistent viewport widget).
- Save per-node or Save All → cell JSON + reload.

### Viewport N-key HUD — **do not regress**
- **Keep the full N menu** (ROBOT LIVE, EXTRUDER, VIEW/Save View, DEV/Dev Mode) — only remove the always-visible edge icon/tab.
- **No always-visible “N” tab** on the left viewport edge — user removed it; do not re-add during overlay fixes.
- HUD is **hidden by default** (`IsSyncHudOpen=false`); press **N** to show/hide the full panel.
- **No slide transform** (`SyncHudTranslateX`) — use `IsVisible="{Binding IsSyncHudOpen}"` only.
- **Save View** and **Dev Mode** live inside the N-key panel (not bottom-left, not top-right).
- `ResetViewportOverlayState()` closes HUD on boot + cell swap.
- **Never** restore the floating “N” border or bottom-left Save View duplicate when reverting overlay code.

---

## Bug fixes

### Robot and cell not showing (threading)
- **Symptom:** Console showed `robot=True bed=True` then crash: `The calling thread cannot access this object because a different thread owns it` on `lfam2.json` load.
- **Cause:** `SwitchCell` background `Task.Run` logged to console and enqueued swap off UI thread; Avalonia objects touched from wrong thread.
- **Fix:**
  - `ConsoleViewModel.Log` / `LogError` marshal to UI thread via `Dispatcher.UIThread.Post`.
  - `MainWindow.axaml.cs` `SwitchCell` posts completion callback with `Dispatcher.UIThread.Post`.
  - `ViewportView.axaml.cs` `WireGlCanvas` retries on `DataContextChanged`; force render after wire.
  - `GlHostControl.Windows.cs` initial size capture + frame on attach.
  - `CellLoader.FindAll` NAS fallback if network cells dir fails.
  - `CellSceneLoader.cs` logging for missing robot/bed paths.

### Boot crash (2026-06-21)
- **Symptom:** App terminated on startup after cell load began.
- **Cause:** `ApplyCellSwap` in `ViewportView.axaml.cs` read `DataContext` on the GL render thread (line ~1223).
- **Fix:** Use the `vm` parameter already passed in; never touch `DataContext` from `OnRender` / GL thread.

### `affecto_staubli.glb` load failure
- **Symptom:** `FileNotFoundException` for `asset-cache\affecto_staubli.glb.bin`.
- **Cause:** File is JSON glTF (not binary GLB) with external `.bin` sidecar; `AssetLocalCache` copied only the 6KB JSON to NAS cache without the 8MB bin.
- **Fix:** `AssetLocalCache.cs` embeds external buffers into a single binary GLB when caching; re-embeds stale JSON-only cache entries (`IsBinaryGlb` check).

### Viewport hidden / all panels expanded (2026-06-21)
- **Symptom:** Could not see 3D viewport; all right-panel sections appeared open; workflow UI stacked over center.
- **Causes:**
  1. `ViewportOverlayView` inherited opaque theme background, painting over the GL canvas.
  2. `SectionExpander` template used `TemplateBinding IsExpanded` on `ContentPresenter` — collapse did not hide content reliably.
  3. LFAM 3 workflow showed param cards for all inactive phases when Live I/O expanded.
  4. Many right-panel expanders had `IsExpanded="True"` hardcoded.
- **Fixes:**
  - `ViewportOverlayView.axaml`: `Background="Transparent"` on UserControl and root Grid.
  - `ViewportView.axaml`: `GlHostControl Background="Transparent"`.
  - `BaseStyles.axaml`: `ContentPresenter` visibility → `{Binding #PART_toggle.IsChecked}`.
  - `RightPanelView.axaml`: sections default `IsExpanded="False"`.
  - `ViewportViewModel.cs`: inactive phase param cards disabled (`Show*ParamCard => false`); only active phase column expands.
  - `Lfam3WorkflowTimelineView.axaml`: Live I/O in `ScrollViewer` MaxHeight 220; workflow overlay MaxHeight 320.
  - `MainWindow.axaml.cs`: apply right-panel column widths on load.

### Viewport overlay clutter — menus always open (2026-06-21)
- **Symptom:** N menu, transform toolbar, edit-points/seam editor, and LFAM 3 phases all visible on boot — viewport blocked.
- **Causes:**
  1. Transform toolbar had **no `IsVisible` binding** — always shown.
  2. LFAM 3 workflow timeline always expanded on LFAM 3 cells.
  3. Overlay `DataContext` could lag behind visual attach; loose bindings defaulted panels visible.
- **Fixes:**
  - `ShowTransformToolbar` — only when a transformable object is selected (not toolpath).
  - `ResetViewportOverlayState()` — closes HUD, seam editor, gizmo, Live I/O; called on boot wire + cell swap.
  - LFAM 3 workflow **collapsed by default** — small bottom chip; click to expand; chevron-down to collapse.
  - `ViewportOverlayView.axaml` + `Lfam3WorkflowTimelineView.axaml`: `x:CompileBindings="True"` + `x:DataType` for reliable visibility bindings.
  - `WireGlCanvas`: always syncs `OverlayView.DataContext`; clears selection on cell swap.

### Phase UI polish (earlier)
- Phase borders sloppy / icons cropped → new column structure, larger icon host, `ClipToBounds=False`.

### LFAM 3 workflow layout — do not regress

| Layer | File | Role |
|-------|------|------|
| Rivet | `Lfam3WorkflowPhaseBlock.axaml` | 52px circle button + phase title chip only — **no** Pick/Deposit, playback, or param cards |
| Column | `Lfam3WorkflowPhaseColumn.axaml` | Fixed 56px layout cell; float hosted in a `Canvas` (sibling of rivet) so it is measured with **infinite height** (no 56px clamp); `Canvas.Bottom="58"` pins the float bottom just above the rivet and content grows **up** |
| Pills | `Lfam3WorkflowPickDepositFloat.axaml` | Pick/Deposit pill group; binds `ToolPanel.PickCommand` / `DepositCommand` |
| Track | `Lfam3WorkflowTimelineView.axaml` | 90px spacer above track; both 3- and 4-phase grids use `Lfam3WorkflowPhaseColumn` |

**Never:**
- Put Pick/Deposit inside the 56px rivet layout tree (stacks, large negative margins) — breaks alignment or collapses float to 0px height.
- Host the float in a height-clamped `Grid`/`Border` cell and lift with a **fixed** `TranslateTransform` — the float height changes (~32px collapsed vs ~240px expanded) and an oversized child **top-anchors and overflows DOWNWARD** in the 56px cell, covering the rivet and running off the bottom of the screen. (This was the bug fixed 2026-06-21 session 6.) A `Bounds.Height`-driven dynamic translate also fails: the cell clamps every measured/arranged height to 56.
- Tie phase detail expansion to `LiveIo.IsExpanded` — use `ToolPanel.ShowPlayback` only.
- Re-add `Lfam3WorkflowPickDepositOverlay` sibling grid — superseded by per-column floats.

**Always:**
- Keep rivet row at 56px; host the float in a **`Canvas`** (no height clamp) and pin its bottom with `Canvas.Bottom` so content grows up. Float `Border` `Width` binds to `#FloatCanvas.Bounds.Width`, content `HorizontalAlignment="Center"`.
- `ClipToBounds="False"` on track, panel, phase column, Canvas, and workflow overlay.
- `NotifyToolChangePanels()` → `NotifyPhaseExpansionChanged()` when sequence state changes.

---

## Test status

Current baseline: **`docs/KNOWN-TEST-FAILURES.md`** (15 known failures, with reasons).
The June-2026 snapshot that used to live here is in `docs/memory-archive.md`.

## Key files (SPSM / mill / STEP — 2026-07-31)

| Path | Role |
|------|------|
| `src/MassiveSlicer.Core/Models/MillBitTool.cs` | Bit library model + Flat 3in AP90 default |
| `src/MassiveSlicer.Core/IO/MillBitLibraryLoader.cs` | AppData `mill_tools.json` load/save |
| `src/MassiveSlicer.Core/Models/MillOperationKind.cs` | Operation catalog + `MillAreaSelectTool` enum |
| `src/MassiveSlicer.App/ViewModels/SubtractiveSettingsViewModel.cs` | BITS / OPERATION / TOOLPATHING / SELECT AREA |
| `src/MassiveSlicer.App/ViewModels/MillBitLibraryViewModel.cs` | Bit library dialog VM |
| `src/MassiveSlicer.App/Views/MillBitLibraryDialog.axaml` | Bit library UI |
| `src/MassiveSlicer.App/Views/RightPanelView.axaml` | Mill StepCards + SELECT AREA tiles |
| `src/MassiveSlicer.App/ViewModels/ViewportViewModel.cs` | Mill area state, brush toolbar, phase select (no TCP) |
| `src/MassiveSlicer.App/Views/ViewportView.axaml.cs` | Mill pointer paint + GL upload of weights |
| `src/MassiveSlicer.App/Views/ViewportOverlayView.axaml` | Bottom mill brush toolbar |
| `src/MassiveSlicer.Viewport/Rendering/MillSurfacePaint.cs` | Soft vertex-weight paint |
| `src/MassiveSlicer.Viewport/Rendering/MeshRenderer.cs` | Paint channel + lime selection overlay shader |
| `src/MassiveSlicer.Viewport/Scene/Picker.cs` | `PickFaceDetailed` / triangle helpers |
| `src/MassiveSlicer.Viewport/Loading/CascadioStepConverter.cs` | STEP → GLB via Python cascadio |
| `src/MassiveSlicer.Viewport/Loading/StepLoader.cs` | Windows STEP entry → cascadio |
| `src/MassiveSlicer.App/Console/ConsoleCommandRegistry.cs` | `mill` command family |
| `scripts/mcp/massiveslicer_mcp.py` | MCP mill tool + command docs |

## Key files (quick reference)

| Area | Paths |
|------|-------|
| Cell load | `MainWindow.axaml.cs` (`SwitchCell`), `CellSceneLoader.cs`, `CellEnvironmentBuilder.cs`, `ViewportView.axaml.cs` (`ApplyCellSwap`) |
| Scene cache | `CellSceneCache.cs` (`Invalidate` for `reload-cell`) |
| Console | `ConsoleViewModel.cs`, `ConsoleCommandRegistry.cs`, `ConsoleView.axaml` |
| Workflow | `Lfam3WorkflowTimelineView.axaml`, `Lfam3WorkflowPhaseBlock.axaml`, `Lfam3WorkflowPhaseColumn.axaml`, `Lfam3WorkflowPickDepositFloat.axaml`, `ToolChangePanelBinding.cs`, `BaseStyles.axaml`, `ViewportViewModel.cs` |
| Live I/O | `Lfam3LiveIoPanelView.axaml`, `LiveIoMonitorViewModel.cs`, `ExtruderBridgeClient.cs`, `MillingModbusClient.cs`, `Lfam3LiveIoCatalog.cs`, `LiveIoPhasePlan.cs` |
| Robot sync / pose | `RobotSyncService.cs`, `RobotPanelViewModel.cs` (`$AXIS_ACT`, `$POS_ACT` stream) |
| Local control bridge | `LocalControlBridge.cs`, `ConsoleCommandRegistry.cs` (`pos`, `readvar`, `set-frame`, `move-pose`) |
| Controller dispatcher | `\\192.168.0.153\krc\ROBOTER\KRC\R1\cell.src` (MS_* + `bRunScanPick`) |
| Milling bridge deploy | `scripts/deploy_bridge_lfam3_milling.py` (GitHub: [scripts/deploy_bridge_lfam3_milling.py](https://github.com/MattWhite3194/MassiveSlicer/blob/main/scripts/deploy_bridge_lfam3_milling.py)) |
| GL host | `GlHostControl.Windows.cs`, `ViewportView.axaml` |
| Overlay | `ViewportOverlayView.axaml` |
| Import | `ImportHelper.cs`, `GltfImportInspector.cs`, `GltfLoader.cs`, `AssetLocalCache.cs` |
| KRL import | `KrlToolpathParser.cs`, `MainWindowViewModel.ImportKrlToolpath`, `ViewportViewModel.AddImportedToolpath` |
| Toolpath render/pick | `ToolpathRenderer.cs`, `SceneRenderer.PickToolpath`, `ToolpathMoveKinds` |
| PBR / MCP materials | `PbrMaterialSettings.cs`, `PbrMaterialBridge.cs`, `scripts/mcp/massiveslicer_mcp.py` |
| Outliner | `OutlinerItemView.axaml`, `LeftPanelView.axaml`, `OutlinerItemViewModel.CanDelete` |
| Screenshot | `AppScreenshotCapture.cs`, `MainWindow.CaptureAppScreenshotAsync` |
| Tests | `CellSceneLoadTest.cs`, `GltfImportTest.cs`, `Lfam3LoadTest.cs`, `KrlToolpathHandlingTest.cs`, `KrlImportOutlinerTest.cs`, `PbrMaterialSettingsTest.cs`, `OutlinerCanDeleteTest.cs` |

---

## Pending / not started

> **The forward-looking backlog now lives in `ROADMAP.md` (repo root).**
> Items below predate it and are carried there; keep new plans in ROADMAP.md.

### SPSM / `feature/spsm` open items (2026-07-31)

1. **Commit + push** remaining uncommitted SPSM work (mill library, cascadio STEP, soft paint, phase no-TCP, MCP mill) — only `8568268` is on origin so far.
2. **Mill area paint → real toolpath region** — weights exist in-viewport; need to feed SELECT AREA into subtractive path generators / stock bounds.
3. **SELECT AREA deeper mesh marquee** if Face/Box/Lasso still feel incomplete vs Brush.
4. **Auto-unwrap quality** for soft paint is world-vertex-based now (good for CAD); optional xatlas if UV-based tools return.
5. **Shop verify:** paint on large STEP; mill bit library dialog UX on both 100% / 125% DPI.

### General backlog (older)

1. **PBR polish (core done — see Completed features):** real metallic-roughness PBR with textures now renders. Remaining nice-to-haves: full prefiltered-env + BRDF LUT IBL (v1 uses roughness→LOD + analytic Karis fit); alpha **blend** ordering (v1 = Opaque + Mask only); populate `UvSettingsViewModel` from the selected mesh; later: apply the material system to toolpath meshes + feed the slicer.
   - **"Make it pop" — DONE via user sliders + default backdrop (2026-06-21):** a *hardcoded* in-shader exposure/IBL boost **grayed** the crystal (it's mostly metal → colour comes from albedo-tinted env reflection → brightening hits ACES's desaturation shoulder). So instead: added user-facing **Exposure** + **Reflections (IBL gain)** sliders in the LIGHTING panel (`ViewportViewModel.Exposure`/`IblIntensity` → `SceneRenderer` → per-mesh `MeshRenderer.Exposure`/`IblGain` → `uExposure`/`uIblGain`, defaults 1.0 = neutral) so the user dials it live. Also set a **non-None default backdrop** (`ViewportViewModel` ctor picks AmbienceExposure4k/CasualDay4K/… from `assets/Images/*.hdr`, fallback first image) so imported models get IBL out of the box. Verified: bumping the sliders brightens + glosses the crystal with colour intact.
   - **Import display:** the committed Final Render is colourful and correct (verified after a clean rebuild). If an imported model looks grey/flat, suspect a **stale NAS build** first (clean-rebuild Viewport), then check that `ApplyShaderModeToSubtree` ran (toggling a shader mode forces it).
2. **Optional cleanup** — delete obsolete `%LOCALAPPDATA%\MassiveSlicer\build2|build3|build4` folders.
3. ~~**KRL import**~~ — **done** (2026-06-25 milestone). Remaining: path-tangent IK for scrub on mill moves with zero normals; optional bead width from tool diameter.
4. **User verification** — confirm: no N tab on boot; N key opens HUD; LFAM3 timeline expands on click; **rivets aligned on connector** (done); **Pick/Deposit pills above active phase** after expand + phase select, details expand **upward** on pill click (session-6 Canvas fix); transform bar; rock select → Focus bar; Live I/O **Position** column shows A1–A6/E1 + TCP when robot synced; P1–P3 I/O live on LFAM 3 (`extIp` 192.168.0.196, `millIp` 192.168.0.249).
5. **Spindle RPM display** — not implemented; would need KUKA spindle `$ANOUT` or ATV340 Modbus (see LFAM 3 Live I/O map).
6. **Large STL import crash (1.5GB+)** — `StlLoader` loads the whole file unindexed/unstreamed, no size guard; crashes on huge files (native access violation, likely OOM/GPU-driver on the resulting multi-GB single buffer). Two fix options logged in the 2026-07-16 changelog entry below (quick safety-net error vs. real streaming/indexed-geometry fix). Not started.
7. **Diagnostic trigger — "arm going through the floor" / "sinking into the platform" / robot rendering below-bed:** before chasing bed-math, GLB bounds, or camera-angle theories, check `CellPaths.cs:14` — a hardcoded NAS UNC fallback (`\\192.168.0.191\...\assets\cells`) is checked *before* the local repo copy, and can silently serve a stale cell file to any machine that can reach that share (confirmed root cause of a real "print sinks ~200mm into platform" bug on 2026-07-16 — stale `lfam1.json` had the pre-fix bed `origin.z`). Console logs `[cell] using cells directory: <path>` at boot; if that's a NAS path instead of the local repo, that's almost certainly it. Per-machine workaround: set env var `MASSIVE_SLICER_CELLS` to the local repo's `assets/cells` path. Not fixed at the code level — the hardcoded fallback is still fragile for anyone else on that LAN; flagged to the team, not yet resolved.

---

## Session changelog (reverse chronological)

### 2026-08-06 — Seam guide and actual seam on different sides of the part

Three reports, two real causes, both in the placement/commit path rather than the slicer.

**1. Guides landed off the part, and repeated clicks stacked identical points.**
The toolpath snap shipped earlier the same day used a *world-space* accept radius (25mm, or
15% of part height). On a 3m cell that is roughly two pixels, so the snap silently never
fired; every click fell through to the held on-wall position and committed the same stale
point. The Guide Points list showed three identical `3044, -309, 163` entries. The slicer
then aligned the seam to that off-part guide — which is why the seam appeared "somewhere
else." **No frame bug:** the slicer receives world-space meshes
(`ViewportView.axaml.cs` ~4217, `TransformPoint(positions[i], world)`) and toolpath
SceneNodes carry no transform of their own, so guide and part share one space.
*Fix:* snap in screen pixels (40px grab, front-most on a pixel tie, since the toolpath draws
as lines and the far wall shows through), plus a 2mm duplicate guard in
`AddSeamGuidePoint`.

**2. Saving a guide did not re-slice.** `SetSeamGuides` raises only `SeamGuideSummary`, which
was absent from `RealtimeSliceProps` in `ViewportView.axaml.cs` (`SeamMode` was there, the
guides were not). Save committed the point and left the toolpath alone, so the green guide
sat next to the yellow seam from the previous slice. *Fix:* added the property to the list.
`ScheduleRealtimeSlice` already refuses to run over a protected baked toolpath and honours
the pause, so project load is unaffected.

**How the slicer uses guides** (`PlanarSlicer.cs:654`, `ContourSeamPlanner.cs`): `ToXY()` —
**Z is discarded**. `NearestGuideToContour` picks *one guide per closed loop*, so multiple
points are meaningful only for multiple islands, never for varying the seam by height. The
guide is consulted **only on a birth layer** (no overlapping parent above the threshold);
every layer above projects the parent's seam onto its own contour for continuity. On a
flaring part the seam therefore follows the surface outward while the guide column stays a
vertical line — they diverge visually by design.

**Key files:** `src/MassiveSlicer.App/Views/ViewportView.axaml.cs`
(`TrySeamGuideOnToolpath`, `RealtimeSliceProps`),
`src/MassiveSlicer.App/ViewModels/ViewportViewModel.cs` (`AddSeamGuidePoint`,
`SaveSeamEditor`).

**Worth remembering:** a screen-picked tool needs a screen-space tolerance. A millimetre
radius that feels right on a benchtop part is invisible on a 3m robot cell.

### 2026-08-06 — Seam edit tool showed no line on a sliced part

**Symptom:** click **Edit** next to Seam position guides and no line appears — the tool
looked dead. Reported the same day auto-slice on import was restored.

**Cause:** not a regression in the seam code — `git diff 1febe52..HEAD` over
`ViewportView.axaml.cs` and `RightPanelView.axaml` showed **zero** seam-related changes since
the version that was confirmed working. The scene state changed, not the code.
`TrySeamGuideOnModel` only accepts a ray/face hit on a non-toolpath node, and
`Picker.PickFaceDetailed` skips any node that is invisible or has no uploaded
`Mesh.PickingData`. Toolpath nodes carry no mesh at all. So once a part is sliced and the
model is hidden, **nothing in the scene is pickable** → no preview column is built → the
render pass at `SceneRenderer.cs:1620` is gated on
`_seamGuidePoints.Count > 0 || _seamGuidePreview.HasValue`, so it draws nothing. With no
guides placed yet, the viewport is simply empty. Restoring auto-slice on import made this
the normal landing state, which is why it surfaced now.

**Fix:** `TrySeamGuideOnToolpath` in `ViewportView.axaml.cs` — when the face pick finds
nothing, snap to the nearest visible **extrusion** point (`ToolpathMoveKinds.IsCutSegment`,
travels excluded). Moves are subsampled to ~20k points because this runs on every mouse
move and a metre-scale part holds hundreds of thousands. The accept radius is
`max(25mm, 15% of part height)` so it behaves the same on a 200mm bracket and a 3m panel;
off-part hover still falls through to the held on-wall position rather than snapping across
the scene. Both the hover preview and the committing click go through this helper.

**Key files:** `src/MassiveSlicer.App/Views/ViewportView.axaml.cs` (`TrySeamGuideOnModel`,
`TrySeamGuideOnToolpath`), `src/MassiveSlicer.Viewport/Scene/Picker.cs:220-224` (the
visible + `PickingData` gate).

**Worth remembering:** when a UI feature "stops working," diff it against the commit where
it was confirmed working *before* reading the subsystem. Zero diff redirects the search from
the feature to its inputs, and it costs one command.

### 2026-08-06 — Auto-slice on import restored (and why it went missing)

**Symptom:** drop in a model with the top-bar toggle on "Realtime" and nothing slices.

**Cause:** `955e898` (2026-08-01, SPSM batch) added an early return to
`ViewportView.RunRealtimeSliceAsync` — *"Only re-slice when a toolpath already exists.
Auto-slicing a fresh import (esp. dense STEP meshes) can freeze or crash."* Correct for
**re-slicing**, but a fresh import has no toolpath by definition, so the same return also
removed the **first** slice and nothing was left to create one. It arrived in the 08-06 pull,
so it looked like a merge regression — it is not.

**Fix:** no toolpath → run a full `RunSliceAsync`. Crash protection kept as a size check
rather than a blanket refusal: above `AutoSliceMaxTriangles` (1,000,000) the import is skipped
and the status bar reports the actual triangle count instead of failing silently. Production
parts here are 145k–160k tris, so normal work clears it ~6×. Confirmed with the SPSM author
that auto-slicing is wanted; anyone who prefers it off can pause the Realtime toggle, which is
the existing control for exactly that. **If a dense STEP import still hangs, lower the
threshold — do not restore the blanket guard.**

### 2026-08-06 — Material presets: per-head calibration, flow-offset leak, user storage

- **Calibration inputs never saved.** `MaterialPreset` stored only `CalibratedOn`/
  `CalibrationNote`, so Edit always reset motor speed / run time / purge weight to defaults.
  Now persisted and round-tripped.
- **"RPM at 100% output" removed** — the operator reads a percentage off the drive and the
  slicer exports a percentage; the extra field only allowed the two to disagree.
- **Calibration no longer hijacks `ExtrusionSpeedOffset`.** `MaterialCalibrationWorkspace`
  forced the motor value through that field to bypass geometry-based flow. It is a field used
  on real jobs, it persists into `prefs.json`, and it then silently added itself to the flow of
  everything sliced afterwards — the likely source of a 65% screw value on a 6×3 bead that
  computes to 27%. Now uses `ExtrusionRpmOverridePercent` and clears the offset; test asserts
  both halves.
- **HV and HF calibrate separately.** `ApplyCalibration` always wrote the HV flow rate, and
  `FlowRateFor()` falls back to HV when HF is 0 — so an uncalibrated HF printed with the wrong
  screw's number, silently. Each head now keeps its own inputs and provenance; the selector
  defaults to the active cell's extruder. Tests: `MaterialCalibrationPerHeadTest` ×4.
- **Library moved** to `%AppData%/MassiveSlicer/materials.json` beside `prefs.json`. The old
  `assets/materials.json` was resolved by searching upward from the working directory, so one
  machine had two libraries (repo and `bin/`) and which you saw depended on how the app was
  launched; edits could be wiped by a rebuild and could reach the team through git. The repo
  file is now read-only seed data. `Save()` no longer swallows errors.

**Related trap seen the same day:** the app rewrote the git-tracked `assets/krl_postprocess.json`
after the KRL post-process dialog was opened, replacing the stored header with the URM one. A
stored header overrides the default (unconditionally on the analog branch), so a snapshot taken
by one person can reach everyone through git. Reverted, not pushed. Shared repo assets should
not be used as live user state.



### 2026-07-31 — `feature/spsm`: Mill BITS/OPERATION/paint, STEP cascadio, no TCP on phase switch

**Branch:** `feature/spsm` @ MassiveMAKE PC (`C:\Users\MassiveMAKE\MassiveSLICER`). Pushed earlier: `8568268`. Rest uncommitted at session end.

**Mill UI (SPSM)**
- Mill sidebar: **1 BITS** (library + Flat 3in AP90 default), **2 OPERATION** (strategy + SELECT AREA), **3 TOOLPATHING** (SpindleRpm linked), MORE catch-all.
- Scan/Mill StepCards match Printing; BACK TO STEPS removed.
- This PC only: default cell **LFAM 3** (prefs local).

**STEP import**
- Dropped Occt.NET runtime path (garbled license MessageBox + crashes).
- Windows STEP = **cascadio** Python (`CascadioStepConverter` + `step-env` venv under AppData).
- Smoke: `CES - C_EXE_2.stp` → ~8k verts OK.

**SELECT AREA paint**
- Soft **world-space vertex weights** (`MillSurfacePaint`), lime green wash in `MeshRenderer`.
- Workpiece-only filter; bottom-center Size/Falloff bar when Brush armed (~250px up).
- No sphere cursor / no right-click brush menu.
- Paint channel isolated from PBR maps (attrib 4 + unit-9 legacy unused for vertex mode).

**Phase switch**
- Print/Scan/Mill sidebar **does not** call `SelectLfam3Tool` → no TCP toolhead selection on phase change.

**Console / MCP**
- `mill status|area|brush size|brush falloff|op …`
- MCP `massiveslicer_mill` + command description update.

**Gotchas logged**
- GLSL must stay ASCII or NVIDIA kills launch on mesh load.
- Prefer Release build from repo bin path on this PC; kill `MassiveSlicer.App` before rebuild if exe locked.

**Key new files:** see “Key files (SPSM…)” table above.

### 2026-07-29 — Drop to Plate slid sideways on LFAM 3 (world-vs-parent frame)

**Symptom (Wes):** on LFAM 3 only, flip a model 180° then Drop to Plate and it moves a foot or
two along **Y** instead of landing on the bed. Rotate itself was fine.

**Cause.** `DropToPlate` computed the drop correctly in WORLD space
(`LayFlatMinZ` and `BedZ` are both world Z) but applied it with a post-multiply:
`node.LocalTransform * CreateTranslation(0,0,dz)`. Under the row-vector convention that lands
the translation in the **parent's** frame. On LFAM 1/2 the bed node is a pure translation, so
parent-Z == world-Z and it worked by luck. LFAM 3 has no flat bed — user imports are parented to
the rotary pivot (`AttachUserImportToCell` → `_rotaryBedPivot ?? _bedNode`), whose frame carries
`baseAbc = [-0.093, 0, -90]` from `lfam3.json`. That −90° roll maps the frame's local +Z onto
world ±Y. Reproduced numerically with the real matrices: old code moved **ΔY +100, ΔZ 0** (never
touched the bed); fixed code moves **ΔZ +100** and lands exactly on it.

**`ApplyLayFlat` had the same defect twice** — it builds a rotation about a WORLD centre and
post-multiplies it, so its rotation pivot drifted on *every* cell (not just LFAM 3), and its drop
step failed identically. The stale comment "Row-vector: W_new = W_old * M" was the tell: that
identity only holds when the parent is identity.

**Fix.** One helper, `ViewportView.ApplyWorldTransformToNode(node, W)`, conjugating a world
transform into the parent frame: **`L' = L · P · W · P⁻¹`**. `DropNodeToBed(node, bedZ)` wraps the
drop. Both `DropToPlate` and `ApplyLayFlat` now go through them. **Any future code applying a
world-space transform to a scene node must use this** — post-multiplying is only safe when the
parent is identity, which is not true on any cell.

Also: `LayFlatMinZ` / `LayFlatWorldCenter` now fall back to `PendingMesh` (the repo-wide
`Mesh?.PickingData ?? PendingMesh` idiom), so a freshly imported mesh can be dropped before GPU
upload. Tests: `DropToPlateFrameTest` ×5 against LFAM 3's real rotary matrices — flat-parent case,
rotary case, purely-vertical motion, flip-180-then-drop, idempotency. Verified in the app on
LFAM 3 by Derek. Suite 543 pass / same 15 known failures.

**Related open work:** `feature/extruder-keepalive` (Caracol screw keep-alive, see the .src gap
analysis) is pushed but **NOT print-verified** — needs a real run before merging.
`tools/check_extruder_gaps.py` is on `main` already and audits any .src for extruder silence.


### 2026-07-28 — Trackpad navigation, Preferences controls, and a preference→re-slice bug

**Trackpad only zoomed; rotate/pan needed a mouse.** Root cause was *not* missing code — the
`Touchpad` navigation preset already existed and worked. Two things hid it:
- `AppPreferences.ActivePreset` defaults to **`Rhino`**, whose wheel branch zooms and ignores
  modifiers entirely — exactly the "only zooms no matter what keys I hold" symptom. Diagnose this
  by reading `~/Library/Application Support/MassiveSlicer/prefs.json` → `ActivePreset`, not by
  re-reading the handler (that mistake cost two round trips).
- The preset's Preferences labels were **wrong on all three bindings** (advertised Ctrl-orbit /
  plain-two-finger-pan / pinch-zoom), so anyone who found it tried the wrong gestures and
  concluded it was broken. Labels in `NavigationPreset.All` must change together with
  `ViewportView.OnPointerWheelChanged` — that is the authoritative mapping.

**Final Touchpad mapping** (per shop request): two-finger = **pan**, Shift + two-finger = zoom,
Cmd + two-finger = **rotate**. Pan default speed raised 4 → 9. Pan is centralised in
`ViewportView.PanFromTouchpad()` so the normal and 2D-slice-plane branches can't drift.
Horizontal **always** follows the fingers; `TouchpadInvertPan` flips the **vertical axis only**
(negating both axes mirrored left/right — a bug shipped and fixed same day).

**New prefs + UI:** `TouchpadPanSpeed` / `TouchpadOrbitSpeed` / `TouchpadZoomSpeed` /
`TouchpadInvertPan` in `AppPreferences`, mirrored on `ViewportViewModel`, applied live via
`SyncViewportFromPrefs`, edited under **Preferences → Navigation → TOUCHPAD GESTURES** (visible
only when the Touchpad preset is active). Preferences window was `Height=480` +
`CanResize="False"`, making lower settings unreachable → now 640, resizable, min 360 / max 1100.
Default preset is still `Rhino` — switching it is a team decision (mouse users would get pan on
plain wheel).

**BUG WORTH KNOWING — editing any preference re-sliced the model.**
`PreferencesViewModel.Commit` → `SyncViewportFromPrefs`, which also re-pushed **every slicing
setting** from prefs into the Additive panel; those assignments raise `PropertyChanged`, which the
realtime-slice watchlist turns into a re-slice. It also **silently overwrote slicing settings
changed in the panel this session** (e.g. resetting `SeamMode` from a stale pref — nasty given the
zig-zag/Normal rule below). Fix: slicing block extracted to `SyncSlicingSettingsFromPrefs()` and
gated behind `SyncViewportFromPrefs(bool includeSlicingSettings = true)`; preference edits pass
`false`, startup/workspace-load still do the full sync. **If you add a settings sync path, keep
slicing settings out of it.**

### 2026-07-28 — Repo working-cost cleanup (for 4–5 devs running AI sessions)

Changes were slow and token-expensive. Measured, then fixed the cheap causes:
- **`docs/CODE-MAP.md`** (new): subsystem → file index plus section anchors inside the oversized
  files. Read costs measured: `ViewportView.axaml.cs` **~178k tokens**, `ViewportViewModel` ~86k,
  `RightPanelView.axaml` ~76k, `LightningPlanner` ~55k, all of `Core/Slicing/` ~223k.
- **`docs/KNOWN-TEST-FAILURES.md`** (new): the **15** baseline failures with reasons (9
  path/CWD-dependent — the count wobbles 14↔15 because `MeshoptDecodeTest` calls
  `Directory.SetCurrentDirectory`; 6 real WIP incl. the two Cut Modifier "Apply does nothing"
  repros). Compare against this list instead of stash-deriving a baseline.
- **`memory.md` 1,221 → ~740 lines**; pre-07-04 entries + build 1–30 log moved verbatim to
  **`docs/memory-archive.md`**. Rotate again past ~800 lines.
- **`.claude/settings.json`** (new, shared): allowlists routine build/test/read-only-git/search so
  nobody is prompted for them. `.gitignore` un-ignores just that file.
- **`CLAUDE.md`/`AGENTS.md`**: efficiency defaults, an explicit "when to read broadly" rule (if you
  can name what you're looking for, grep; if not, read the whole thing), a deep-dive escalation
  protocol with the measured cost table, and multi-developer git prompts (pull before first edit;
  branch for slicer/KRL/schema/multi-session work; land or park a branch before switching
  features; merge `main` daily). Keep the two files identical.

**Branch hygiene note:** `feature/cut-modifier` is 0 ahead / 18 behind — fully merged, safe to
delete. `feature/presets` is 5 ahead / **80 behind** and its presets logic was already
cherry-picked onto `main` on 07-23; needs an owner decision.

### 2026-07-27 (later) — Seam position guides made usable (on-wall vertical columns)

We already had Caracol/Eidos-style seam placement (SEAM → LOCATION → "Seam position guides" →
Edit, `SeamGuidePoint` → `PlanarSlicer` line ~654) but nobody could use it: guides drew as a
**4mm sphere (7mm selected)**, which on a 2.8m panel is ~2 screen pixels. You clicked blind and
selecting a guide looked like a no-op. Reworked the editor's visuals and hit-testing only —
**no slicer behavior changed**, and existing `.mass` guides keep working.

- **Guides render as full-height vertical columns** (`SeamGuideColumnRenderer`, new). This is the
  honest picture: the slicer uses only a guide's XY (`SeamGuidePoints.Select(g => g.ToXY())`), so
  one guide re-seams *every* layer bottom to top. Radius scales with part height (0.22%, min 4mm)
  and shading is nearly flat so it reads as a drawn line, not a 3D tube.
  `SeamGuideRenderer` is deliberately untouched — it is a shared **sphere** renderer also used by
  curved-boundary and sequence-path markers (changing it broke `SequencePathRenderer`; don't).
- **Colors:** yellow = hover ghost (un-placed), green = placed, brighter green = selected.
- **Always on the wall.** Hover/click/drag share one resolver: exact ray/face hit over the model,
  holding the last on-surface point once the cursor leaves the silhouette. An earlier
  nearest-sampled-vertex fallback jittered between scattered vertices and could land off the
  visible wall — removed. Drag previously slid on a flat Z-plane and could pull a guide off the
  surface; it now rides the wall too.
- **Column height = the actual part.** Toolpath nodes carry no mesh, so a model-AABB-only range
  found nothing whenever the model was hidden (the normal state after slicing) and fell back to a
  fixed bed+1000mm stub — columns overshot the part. Range now also spans visible toolpaths.

**Known gap (not built):** guides are a single XY, so the seam is always perfectly vertical.
Seam staggering/randomizing and height-varying seam paths (a guide *path* rather than a point)
would be additive to this system — see Eidos parity notes.

### 2026-07-27 — Field diagnosis: two "gaps in the toolpath" failure signatures (Scene 08 panels)

Two different diseases produced near-identical team reports of "gaps / printing in mid air".
Diagnosed by dumping the stored toolpath from the shared `.mass` files (no re-slice needed):

1. **Overhang (design problem)** — Panel 02: surface flares curve to ~5° from horizontal in the
   upper third; in single-bead Surface mode consecutive beads land ~33mm apart in XY (bond limit
   ≈ half a bead, 4mm). Signature: fanned ribbons on shallow surfaces; unsupported-bead bands of
   25–50% (measured Z 955–1340). No orientation fixes it (flares face multiple directions —
   best whole-part rotation only 3.7%→3.0% bad area). Fix: cut at Z≈945 (bottom piece 0.2% bad,
   prints as-is), lay top piece back ~44°, or steepen the design.
2. **Zig-zag seam on an enclosed model (mode misuse, reads like a slicer bug)** — Panel 04:
   Zig-zag is *single-skin* mode by design (`PlanarSlicer.ExtractSingleSkinOpenFaces`): each
   closed loop is cut to its longest open arc (thin panels = one skin pass, correct). On an
   enclosed solid this amputates the perimeter; with same-layer travel off,
   `KeepLongestOpenFaceOnly` keeps ONE island per layer → layers jump between regions,
   islands flicker, mid-air starts everywhere. **Field rule: Zig-zag = single-wall/open panels
   only; enclosed or multi-island models = Normal seam.** (Team-confirmed: switching to Normal
   fixed Panel 04 and, retroactively, the Drone.)
   **FIXED same day (guard + warning):** `ExtractSingleSkinOpenFaces` now takes bead width and
   keeps any ring whose mean width (2·area/perimeter, `AverageRingWidth`) exceeds 4×bead closed —
   walls up to ~4 beads thick still single-skin (ZigZagSingleSkinTest's 20mm wall / 6mm bead),
   enclosed solids (mean width tens–hundreds of mm) stay intact. `KeepLongestOpenFaceOnly` no
   longer prunes when any closed ring remains (it deleted whole islands). New
   `Toolpath.Warnings` carries slicer warnings through post-processing (re-stamped like
   FormboundStats in ViewportView) and shows as a red status-bar alert on Slice/Update complete:
   "Zig-zag seam is a single-wall mode… use Seam mode 'Normal'". Tests:
   `ZigZagEnclosedGuardTest` ×6; suite 539 pass / same 14 pre-existing failures as baseline.
   Enclosed models under zig-zag now slice like Normal (closed loops + alternating direction)
   instead of amputating — but the honest recommendation stays: use Normal.

Also recurring: `.mass` files shared without their `workspace_meshes/` sidecar folder can be
viewed (stored toolpath) but not re-sliced ("Update failed: source mesh has no geometry") and
hide the actual sliced mesh from diagnosis — always share the `.mass` + sidecar together.
Backlog: unsupported-bead validation check (would auto-flag both failure modes at export).

### 2026-07-23 (later) — PRESETS merged into MODEL as a nested, collapsed-by-default sub-section

Following the presets sync (below), merged the PRESETS card into MODEL per Jeff's request:
PRESETS is no longer its own top-level workflow step — it's now a plain `SectionExpander`
("PRESETS", no step-number badge) nested inside "1 MODEL", collapsed by default. It persists
its own open/closed state across restarts for free via the existing app-wide `PersistExpander`
behavior (keyed off its plain-string header — same mechanism as ROBOT NETWORK/BED SETTINGS),
no new plumbing needed. MODEL's existing auto-open-on-import behavior is untouched; the nested
Presets section deliberately does NOT auto-expand alongside it (Jeff: "keep auto opening
[Model], but don't auto-expand preset too"). Retired the now-dead `StepPresetsExpanded` VM
property. Also fixed stale intro copy claiming presets are "still in-memory only" — no longer
true post-sync. Verified via build + a live bridge smoke test (imported a test cube, confirmed
MODEL auto-opened with PRESETS collapsed underneath, no duplicate blocks).

Key files: `src/MassiveSlicer.App/Views/RightPanelView.axaml`,
`src/MassiveSlicer.App/ViewModels/RightPanelViewModel.cs`.

### 2026-07-23 — Synced real Presets logic from `feature/presets` onto `main` (Jeff, w/ Claude)

`feature/presets` had gone stale (only 5 real commits ahead of its branch point, vs. 62 commits
`main` had raced ahead with — Cut Modifier, effector UX, etc.), so a straight branch merge risked
conflicts across files the branch never touched but that had since been heavily reworked
(`GizmoRenderer.cs`, `PlanarMeshSplitter.cs`, `SceneRenderer.cs`, `SceneNode.cs`). Cherry-picked
instead, on a throwaway `sync/presets-to-main` branch off `main`:

- Skipped `4eddb94` ("Add PRESETS card...") — `main` already had this exact content committed
  independently as `b94a280` (same message/date, different hash from a prior merge). Verified
  `PresetsCardViewModel.cs` and `PrintPresetsLoader.cs` on `main` were byte-identical to that
  shared base before touching anything, so nothing since had drifted.
- Skipped `9f97512` (GL-thread DataContext crash fix) — same bug class already fixed on `main`
  via `ee6dbf0`; confirmed `main`'s `ViewportView.axaml.cs` already reads `_vm` at those lines.
- Cherry-picked `a4cd4b2` (real field-group schema — Save/Apply only touch fields a preset
  actually captured, plus a real "Master Defaults" preset) and `dd7986a` (Info popup now shows
  every captured field-group instead of a fixed 7).
- First cherry-pick attempt (of `4eddb94`) silently duplicated the whole "0 PRESETS" `Expander`
  block in `RightPanelView.axaml` (two copies both bound to `StepPresetsExpanded`) — caught via
  `grep -c` before it went further, reset, and re-planned around skipping that commit entirely.
- Verified clean: `dotnet build` (0 errors), then a live smoke test via the local control bridge
  (`build-and-run.ps1` → `GET /screenshot` @ :8723) — Presets card renders once, no duplication,
  and shows real persisted presets loaded from disk (`Master Defaults`, `Thom HHN Nasty Wall`),
  confirming `PrintPresetsLoader.cs`'s real save/load path actually works end-to-end.
- Merged `sync/presets-to-main` into `main` (`0450a3b..ef6c9ec`, fast-forward, no conflicts) and
  pushed. `feature/presets` itself left untouched on GitHub — not deleted, just superseded.

Key files: `src/MassiveSlicer.App/ViewModels/PresetsCardViewModel.cs`,
`src/MassiveSlicer.Core/IO/PrintPresetsLoader.cs`, `src/MassiveSlicer.App/Views/RightPanelView.axaml`.

### 2026-07-21 — Branch model simplified: `master` is gone, `main` is everything
- Upstream cleanup (Thom's side): **`master` and the old merged feature branches were deleted from GitHub**; `origin/HEAD` now points at `main`. The 2026-07-04 master/main split convention below is **obsolete history**.
- **New mode of operation:** `main` is the single shared branch everyone pulls from and pushes finished work to. Unproven/risky work lives on `feature/<name>` branches (currently `feature/cut-modifier`, `feature/presets`); keep them fresh by merging `main` in, and merge them back to `main` only after real-machine testing. Nothing on a feature branch is in anyone else's build until it's merged.
- CLAUDE.md / AGENTS.md git conventions updated to match.

### 2026-07-21 — Model lost when switching to LFAM 3 (cell-swap frame fix)
- **Symptom (team report):** a model loaded on LFAM 1/2 survives switching between those two cells, but switching to LFAM 3 leaves the bed blank ("I think that is by design" — it wasn't).
- **Cause:** cell-swap content transfer re-based preserved models/toolpaths against the raw scene-node transform of `(_rotaryBedPivot ?? _bedNode)`. LFAM 1/2 bed nodes are translation-only so that worked; LFAM 3's rotary pivot (`RotaryBed_Top`) lives in the rotary GLB's tilted mesh frame (baseAbc, e.g. C=−90) at the mesh origin, so the transfer delta rotated the part ~90° and dragged it to the turntable's mesh origin — off the bed, though still listed in the outliner.
- **Fix:** transfer now uses a world-aligned frame at each cell's `Bed.ImportSurfaceCenter` (the same anchor fresh imports land on) — the delta is a pure translation, so parts stay upright at their offset from the print-surface centre. `ViewportView.axaml.cs`: new `ImportSurfaceFrame()`, captured in `ApplyCellSwap`, applied in `RestoreUserContentAfterCellSwap`.
- Also resolved committed merge-conflict markers in this file (header "Last updated" + changelog section, left over from the 07-16 two-sided merge).

### 2026-07-16 — Auto build numbers, RPM calibration inputs, launcher TFM fix
- **Build identity is now auto-generated** (`GenerateBuildInfo` target in MassiveSlicer.App.csproj): build number = git commit count, shown as `build N · date · sha`. Hand-edited `BuildInfo.cs` deleted. Same number on every machine; `git log --oneline` maps builds → commits.
- **Calibration dialog takes true RPM** (read off the extruder drive) plus a one-time "RPM at 100% output" drive scale (default 100 — on our machine %==RPM, e.g. 60% = 60 RPM). `CalibMotorPercent` remains as a computed property for the calibration-scene generator.
- **macOS launcher fix:** the Zivid-optional merge changed the macOS TargetFramework to `net8.0`; `tools/make_macos_app.sh` now resolves the correct bin dir (it was silently launching a stale `net8.0-windows` build).
- Synced with master repeatedly (UI/UX overhaul, TreeSupport/Formbound, Zivid-optional). Note: one upstream history rewrite (force-push) was adopted after verifying content-identical.

### 2026-07-16 — Windows launch crash fix + `.mass` drag-and-drop (Jeff, Windows side, w/ Claude)

**Boot/render crash — same class as the 2026-06-21 "Boot crash" fix, different call sites:** `ViewportView.OnRender` — the TCP-readout guard and cut-tool gizmo check (~lines 1467/1470) read Avalonia `DataContext` directly from the GL render thread instead of the established `_vm` cache field (see the class-level comment at ~line 115: "set on the UI thread in WireGlCanvas, read from GL thread in OnRender"). Crashed on every launch: `InvalidOperationException` — "the calling thread cannot access this object because a different thread owns it." Looks like newer cut-tool/TCP-readout code just didn't follow the existing pattern. **Fix:** swapped both `DataContext` reads to `_vm`, matching every other spot in `OnRender`.

**`.mass` drag-and-drop added:** `ViewportView.OnDrop` only recognized mesh imports (OBJ/3MF/STL/etc via `ImportHelper`) — dragging a `.mass` workspace file onto the viewport silently did nothing. Added a check: a dropped `.mass` path now calls `vm.Erp.OpenWorkspaceFile?.Invoke(path)`, the same open-workspace codepath File → Open already uses (no unsaved-changes prompt — matches existing behavior, not a new gap).

**Known issue — NOT fixed, just diagnosed — large STL import crashes the app:** dropping a ~1.5GB STL crashes with a raw `0xc0000005` access violation (no managed stack trace, "unknown module" — native-level, not a clean .NET exception). Root cause: `StlLoader.ReadBinary` (`MassiveSlicer.Viewport/Loading/StlLoader.cs`) loads the entire file in one shot into two flat, non-indexed `Vector3[]` arrays (every triangle stores its own 3 verts, no dedup) with zero size guard or streaming — a 1.5GB file (~30M triangles) is 2+GB of managed arrays, then one giant single-shot GPU buffer upload. Plausible OOM or GPU-driver failure on the huge single allocation. Two fix options, neither built:
1. **Quick safety net** — catch it, show a clean in-app error instead of crashing (doesn't remove the size ceiling).
2. **Real fix** — stream the file instead of loading it whole, dedupe vertices into indexed geometry, chunk the GPU upload (bigger change, actually solves it).

Workaround for now: simplify meshes outside the slicer before importing.

Key files: `src/MassiveSlicer.App/Views/ViewportView.axaml.cs`, `src/MassiveSlicer.Viewport/Loading/StlLoader.cs`.

### 2026-07-16 (later 2) — URM re-latch guard (extruder-stays-cold fix)

**Field:** Rev05 exported fine (header identical to a known-good file) but the extruder never
heated. Cause was NOT the file — the KUKA ANALOGHANDLER converter had latched at zero: a prior
program end left $ANOUT=0 while T1/T2/T3 still read the target, so setting the same target
produced no change and it never re-wrote. Confirmed live: T1=250 but $ANOUT[1]=0; nudging T
240->250 immediately restored $ANOUT to 0.2912/0.3232.

- **Slicer fix (firmware-independent):** URM header MAT now nudges temps to `target-5C`
  (floored 150), `WAIT SEC 0.4`, then the target — forcing ANALOGHANDLER to re-latch every
  print from any stuck state. target-5 stays hot if a print pauses on the nudge line. New
  placeholders `{{TEMPn_NUDGE_C}}`. Verified this unsticks even the pre-self-heal converter.
- **KUKA-side complement (staged, needs cold boot):** ANALOGHANDLER.sub self-heal reads the
  actual $ANOUT each cycle so external zeroing is caught in ~12ms. Belt-and-suspenders with the
  slicer nudge.
- Header/footer are identical between the "broken" and "working" files — the export was already
  correct; the standard is unchanged apart from adding the re-latch guard. Test: DSS test asserts
  the nudge precedes the target. 410 pass / 13 pre-existing.


### 2026-07-16 (later) — Brim over-sampling fix + Header/Footer gear menu

**Field failure:** brim caused robot jitter/over-extrusion (Wall 03 Panel 01). Cause: `BrimPlanner`
emitted round-join offset loops WITHOUT simplification (the wall contours are simplified, the brim
wasn't), so the brim was sub-mm point spacing (down to 0.01mm) at constant RPM — the robot stalled
at every point while the screw kept pumping. A field decimation of the live SRC (Douglas-Peucker
0.4mm) cut the brim run 4938→529 pts (0 segments below the ~1.4mm robot IPO limit) and fixed it.

- **Root-cause fix:** `BrimPlanner.Apply` now `Clipper.SimplifyPaths(rings, max(SimplificationTolerance, 0.3))`
  after `InflatePaths`, matching the wall-contour treatment. Regression test asserts no brim segment
  < 0.25mm. (6 BrimPlannerTest total.)

**Header/Footer gear menu (user request):** the KRL Post-Processing window (Rules/Header/Footer
tabs, editable raw templates) already existed but its open-handler was orphaned — no button.
- Added a **⚙ gear** (mdi-cog-outline) on the "KRL EXPORT" header in PRINT TOOLPATH →
  `OnKrlPostProcessClicked`. Header tab pre-fills the effective template so `$ADVANCE=5`,
  `$APO.CVEL={{APO_CVEL}}`, `$ACC.CP=5.0`, `$VEL.CP`, the MAT block and CaracolSafety are visible
  and editable; edits persist via `KrlPostProcessLoader.Save` on close.
- **URM now honors edited header/footer** (previously hardcoded the Caracol default and ignored
  edits): `KrlExporter.WriteHeader/WriteFooter` use `s.HeaderTemplate/FooterTemplate` when it is
  still URM-shaped (contains `CaracolSafety` / `EXTRUDER MOTOR COMMAND`), else fall back to the URM
  default so URM can never export an ANOUT header by mistake. `ViewportView` stops nulling the
  template in URM mode. Test: `Urm_honors_edited_header_and_footer_but_falls_back_if_not_urm`.
- Suite 410 pass / 13 pre-existing failures; app builds clean.


### 2026-07-16 (later) — Brim feature (bed adhesion, encloses X-bracing)

**Scope:** New collapsible **BRIM** group under PATTERN AND TEXTURE → EFFECTS (after X-BRACING).
Outward offset loops around the first layer for bed adhesion; user sets loop count.

- `Core/Slicing/BrimPlanner.cs` (new): footprint = Clipper2 dilate+union of the ACTUAL layer-0
  extrude segments (bead/2, round joins) → loop k centreline = edge + (k−½)·bead
  (`InflatePaths`, outer rings only). Emitted outermost→inward, prepended to layer 0 so the
  brim prints first and the innermost loop fuses to the first bead; final travel reconnects
  to the original layer start.
- **Applied as the LAST toolpath step** in `PlanarSlicer.Slice` (after paint removals /
  X-bracing / patterns) so first-layer additions are enclosed — verified by test with a
  protruding segment. Planar slicer only (angled planes have no bed-planar layer 0).
- Settings: `SliceSettings.BrimEnabled/BrimLoops(=3)`; `AppPreferences` +
  `AdditiveSettingsViewModel` (`BrimEnabled`, `BrimLoops` clamp 1–50, `ShowBrimControls`);
  wired through MainWindowViewModel copy blocks, ViewportView SliceSettings build, and the
  re-slice trigger list.
- Tests: `BrimPlannerTest` ×5 (disabled no-op, prepend+survive, loop count, outside+ordered
  outermost-first, encloses protrusion). Suite: 408 pass / same 13 pre-existing failures.

### 2026-07-16 — URM output fix (OUT[8]) + calibrated travel defaults (T5)

**Scope:** Field debugging on LFAM 2 found the URM/Digital-Start-Stop export used the **wrong
output**: the Caracol slide deck says OUT[9]=URM, but on the actual LFAM machines (verified by
live pendant-toggle tests 2026-07-13/16) **OUT[8] → DI_01_URM (ultra-responsive request)** and
**OUT[9] → DI_01_MIO_req (robot-mode gate)**. Exported URM files pulsed the *gate* around travels
and never latched it → CARACOL ignored all temp/RPM setpoints (setpoint 0, deadlock at
`WAIT FOR $IN[6]`). Fixed at the source.

**Machine-verified extruder signal map (LFAM 1 & 2):**

| Signal | Role |
|--------|------|
| `$OUT[7]` | screw strobe / print enable → `DI_05_startPrinting_req` |
| `$OUT[8]` | **URM request** (pulse TRUE only around travels) → `DI_01_URM` |
| `$OUT[9]` | **robot-mode gate** (latch TRUE for the whole job in MAT) → `DI_01_MIO_req` |
| `$IN[5]` | fire alarm (Antincendio) |
| `$IN[6]` | extruder ready ← `DO_06_extruderReady` |
| `$IN[7]` | Effecto QS anti-collision breakaway |

**Changes:**
- `KrlExporter.cs`: `DefaultUrmHeaderTemplate` inits `$OUT[8]=FALSE` and **latches `$OUT[9]=TRUE`
  in MAT**; `EmitCaracolSsPreTravel`/`EmitCaracolSsPostTravel` pulse `$OUT[8]` (not 9);
  `DefaultUrmFooterTemplate` clears OUT[8] (URM) then OUT[9] (gate); doc comments updated.
- **App defaults = T5 winner** from the LFAM 2 8-cell travel calibration (2026-07-16, 15 mm/s
  print / 3 mm layers): `AdditiveSettingsViewModel` — travel **600 mm/s**, wipe **Same-Direction**,
  length **12 mm**, ramp **4 mm**, wipe speed **600 mm/s**, z-hop **3 mm**, resume pause **0.5 s**.
  (T4=250 ms lost narrowly to T5=500 ms; next A/B: 300–400 ms.) Core `SliceSettings` left
  library-neutral on purpose — recommended values live at the app layer.
- `KrlExporterTest.cs`: URM assertions now expect OUT[8] pulses + latched OUT[9] gate.

**Tests:** 403 passed; 13 failures are pre-existing WIP/environmental (verified unrelated —
same set fails with defaults reverted). App builds clean.

**Ops note:** the broken mapping shipped in `SS 8-cell matrix Rev09` — fixed by hand on the
LFAM 2 D:\ share the same day; controller-side complements: sps.sub URM-latch guard, ANALOGHANDLER
$OV_PRO speed-scaled RPM + self-heal, ID3 submit re-registration (see LFAM install session notes).


### 2026-07-12 — 2D Slice Plane Viewer + edit multipass + Target Support Selections

**Scope:** Long iterative session on the **2D Slice Plane Viewer** (edit mode), multipass layer stack, navigation, selection, Formbound “Target Support Selections”, LFAM 1 bed BASE alignment, and ortho zoom clipping. Work tree: `/Users/thomboessel/MassiveSLICER V3`.

#### 2D Slice Plane Viewer (edit mode only)

| Item | Behavior / location |
|------|---------------------|
| Toggle | Layers-triple button under edit pencil (`ViewportOverlayView.axaml` → `IsSlicePlaneViewerActive`) |
| Default ON | Entering paint edit in Preview sets `IsSlicePlaneViewerActive = true` (`ViewportViewModel.IsPaintEditOpen`) |
| Session | Saved/restored via `WorkspaceUiSession.IsSlicePlaneViewerActive` (`.mass`) |
| Camera | Top-down orthographic, elev **90°**, azimuth = rail-up then **−90°** (90° CCW on screen); no orbit |
| Nav lock | Pan + zoom only: block orbit; right-drag pans; Space+LMB pan; trackpad no-mod = zoom not orbit |
| Multipass draw | Current solid 2× width; below −1/60%, −2/30%, −3/17%; +1 dashed ~40% (`SceneRenderer.DrawSlicePlaneLayers`) |
| Pick window | **Active layer only** (not −3…+1) — `GetPaintScrubMoveStart/Limit` |
| Scene hide | Robot/env/solids hidden every frame; contact shadows off |
| Grid | Subtle white measurement grid after toolpaths; blend on; spacing ≈ bead (not half-bead sheet) |
| Layer follow | Scrub layers/timeline slides camera **Target** by layer-centroid delta; **Radius (zoom) preserved** |
| Stats HUD | Left overlay: `SlicePlaneStatsHeader/Body/Below` via `SliceLayerAnalyzer` |
| Workspace | Also: `ShowMultiPlanarPlanes` nullable in `WorkspaceUiSession` so Planes toggle persists |

**Critical multipass bug fixed:** With edit open + scrub active + nothing selected, `SimRenderProgress` returned 0–1 and skipped multipass (`ToolpathSimProgress >= 0`). Multipass now always runs when `SlicePlaneViewerActive`; edit/slice force `SimRenderProgress = -1`.

**Ortho zoom clip fixed:** Zoom used to shrink **eye distance** and frustum together → multiplanar Z span near-clipped. `OrbitCamera`: orthographic eye stays far (≥ 50 m); `Radius` only scales frustum; min ortho zoom 2 mm; depth window ±25 m around focus.

**Key files:** `ViewportView.axaml.cs`, `ViewportViewModel.cs`, `ViewportOverlayView.axaml`, `SceneRenderer.cs`, `GridRenderer.cs`, `ToolpathRenderer.cs`, `OrbitCamera.cs`, `WorkspaceDocument.cs`, `WorkspaceService.cs`, `SliceLayerAnalyzer.cs`.

#### Edit selection (2D slice)

- Single click → short local section; **double-click** → full connected path (`ExpandFullConnectedPath`).
- Slice Path mode: looser `ExpandLocalSection` (multiplanar gaps).
- Esc deselect; selection list / MODIFICATIONS cards for Support/Remove Apply.

#### Formbound — **Target Support Selections**

- Checkbox under Formbound Bridge/Buttress: `LightningTargetSupportSelections` (UI label **Target Support Selections**).
- **On:** skip all automatic geometric Formbound demand; only edit-mode **Support** paint (Bridge marks) drive support (`LightningPlanner` → `goto ManualDemandOnly`).
- **Aggressive full-line coverage** when on:
  - Dense Support dabs (~0.4× bead, along segments) in `MarkPickedSpanDabs`
  - Wider `ProjectBridgeMarks` half-band (planar + angled slicers)
  - Dense-resample painted run; multi-T columns along the line; faster bar growth; corbel reach ~8× bead; coverage audit + reseed; Bridge mode multi-finger along run
- Prefs/settings: `SliceSettings`, `AppPreferences`, `AdditiveSettingsViewModel`, clone/load/save in `MainWindowViewModel`, reslice watchlist in `ViewportView.axaml.cs`.
- Workflow: enable checkbox → select path(s) → Support/Apply → reslice. Re-apply Support after enabling aggressive density if marks were sparse.
- Aggressive mode: **no mid-air births** — lower layers inherit + MaxStep extend only; corbel pads disabled; paint elbows forced wall→core.
- **Target Support Selections isolation:** Formbound runs only when Formbound-style Support paint exists; layers without paint trees use **normal shells**; emit is `localSupportOnly` (no global fillet/island-weld/phantom drop; only Manual/PaintColumn trees notch). FILL PATTERN Formbound alone does **not** scar the whole part when this checkbox is on.

#### Per-area support types + Tree Support (2026-07-12)

- **`PaintSupportStyle`** on each Bridge mark: `FormboundButtress` | `FormboundBridge` | `Tree` (not only global FILL PATTERN).
- Edit Apply / MODIFICATIONS **Support type** stamps style on marks; changing a card restamps **that** mod’s marks only.
- Reslice **force-enables** Formbound/Tree from paint even when FILL PATTERN is None.
- **Tree Support:** `Slicing/TreeSupport/*` — bed-rooted dual-wall branches, MaxStep growth, cluster branching; paint-only v1. Planar + Angled + Multi-Planar emit after shells/Formbound.
- Bridge target UI hidden for Tree (roots to bed). Formbound still uses Target/foot.

#### Console / diagnostics

- `paint support` / `paint support layer` — evaluate edit selection or current-layer islands vs layer-below XY gap (`ConsoleCommandRegistry`).
- Local control bridge `http://127.0.0.1:8723` for `viewset`, `tpdump`, `selection`, screenshots.

#### LFAM 1 print bed / BASE marker

| Concept | Meaning (LFAM 1) |
|---------|------------------|
| Mesh | `assets/cells/LFAM1/lfam1_bed.glb` placed at `bed.origin` |
| Grid corner | `VisualGridCorner` → origin when no visual shift |
| BASE marker | `BaseMarkerWorld` = robot + `baseData.XY`, **Z = origin.Z** |

- Dev-mode translate of Print Bed writes **`bed.origin`** via `CellDevTransformSaver` / `SaveBedDevTransform` (often **Debug bin** cell path, not only repo assets).
- Example move measured: origin **+130.3 X, +99.3 Y** (bin JSON); BASE does not follow bed drag unless `baseData` updated.
- Aligned BASE to geometry: set `baseData.XY = origin.XY` (was +151.15 X offset historically from `1496.36` vs `1345.21`).
- After user bed move, origin/baseData ~ `(1475.51, -609.30, …)` in assets + bin copies — reload cell after edit.

**Bed files:** `assets/cells/LFAM1/lfam1.json`, `lfam1_bed.glb`; runtime may load `bin/Debug/net8.0/assets/cells/LFAM1/`.

#### Planes toggle persistence

- `WorkspaceUiSession.ShowMultiPlanarPlanes` (`bool?`) — capture/restore so Multi-Planar **Planes** overlay state survives `.mass` open; null on old files keeps default on.

#### Practical notes for next session

- 2D slice: multipass must not depend on outliner selection; scrub must be armed (`EnsureScrubArmedForEdit`).
- Dev bed edits: verify both **repo** `assets/cells/...` and **bin** cell path; rebuild can overwrite bin from assets.
- Target Support Selections: requires **Support Apply** marks, not bare selection alone.
- GLSL: no non-ASCII in shader strings (premature EOF).

---

## Older history

Session changelog entries for **2026-07-04 and earlier**, plus the **build 1–30 log**,
moved to **`docs/memory-archive.md`** (verbatim, nothing deleted) to keep this file
skimmable. Add new entries at the top of "Session changelog" above.
