# MassiveSLICER V3 — Project Memory

## ⚠️ Active work: `feature/spsm` (Scan–Print–Scan–Mill) — NOT fully merged to main

**Branch:** `feature/spsm` (tracking `origin/feature/spsm`). First push: `8568268` (default cell prefs + Scan/Mill StepCard chrome). **Large pile of uncommitted SPSM work** still local (mill bits library, STEP via cascadio, mill area soft-paint, phase-switch fix, MCP mill commands) — commit when Jeff wants.

**This PC (MassiveMAKE shop workstation):** machine-local defaults only (do not commit personal prefs):
- Default cell: **LFAM 3** (`AppPreferences` / `DefaultCellName`)
- Windows GPU High Performance for `MassiveSlicer.App.exe` when set via registry earlier
- Mill tool library: `%LOCALAPPDATA%\MassiveSlicer\mill_tools.json` (v3 schema)
- STEP converter venv: `%APPDATA%\MassiveSlicer\step-env` (`numpy` + `cascadio`)

Last updated: **2026-08-21** (code-editor-inject merged onto main)

---

## Convention — triad / gizmo axis colors (LOCKED)

**Canonical MassiveSLICER:** standard robotics/CAD **RGB = XYZ**.

| Axis | Color | Approx hex / RGB | Where |
|------|--------|------------------|--------|
| **+X** | **Red** | `#E63838` · `(0.90, 0.22, 0.22)` sticks · gizmo `(1.00, 0.20, 0.20)` | TCP / FLANGE / SENSOR sticks, world origin axes, Move/Rotate/Scale gizmo |
| **+Y** | **Green** | `#38CC4D` · `(0.22, 0.80, 0.30)` · gizmo `(0.20, 1.00, 0.20)` | same |
| **+Z** | **Blue** | `#4073F2` · `(0.25, 0.45, 0.95)` · gizmo `(0.30, 0.50, 1.00)` | same |

**Source of truth in code (must stay in lockstep):**
- `src/MassiveSlicer.Viewport/Rendering/AxisRenderer.cs` — comment + default verts (“X = red, Y = green, Z = blue”)
- `src/MassiveSlicer.Viewport/Rendering/TcpAxisLabelLayout.cs` — tip labels `x`/`y`/`z` + `ColorX/Y/Z`
- `src/MassiveSlicer.Viewport/Rendering/GizmoRenderer.cs` — `ColX` / `ColY` / `ColZ`

**Labels on the triad:** small **x / y / z** at tips use the same colors. Frame titles (**TCP**, **FLANGE**, **SENSOR**) are white/grey — not axes.

**Not axis colors (do not confuse):**
- **Green spindle bit / cutter cylinder** = tool mesh preview, **not** +Y
- Toolpath density / seam / direction arrows often use yellow/orange — **not** axes
- Orca-style green→yellow→red bead thickness heatmaps — **not** XYZ

**What looks like “inconsistency” (usually not a color swap):**
1. **Wrong frame** — FLANGE vs TCP vs SENSOR triad orientation differs; colors still mean that frame’s local X/Y/Z.
2. **glTF vs KUKA local** — mesh axes can look rotated; sticks are KUKA/tool frame after display correction, not raw glTF bone colors.
3. **TOOL CONVENTION** (Z−/Z+/X−/X+) remaps how taught ABC is *shown* on the TCP triad — still RGB=XYZ in the displayed frame.
4. **Two RGB sticks** near the wrist (TCP + FLANGE) — same color map, different origins/orientations.
5. **KUKA pendant** HMI also uses RGB≈XYZ for BASE/TOOL frames; if pendant and Slicer disagree, check BASE # / TOOL # / IPO frame, not the color legend.

**Agent rule:** When describing directions (“move +X”, “tool +Z into the part”), always map to **red / green / blue** with this table. If UI or a PR paints X green or Z red, treat it as a **bug** and fix toward this convention — do not invent a second legend.

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
| Repo (MassiveFILES / this Mac) | `/Volumes/MassiveFILES/Research/LFAM/MassiveSLICER` |
| Repo (shop PC) | `Z:\Research\LFAM\MassiveSLICER` (same MassiveFILES tree — **not** `C:\Users\MassiveMAKE\MassiveSLICER`) |
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
Set-Location 'Z:\Research\LFAM\MassiveSLICER'
dotnet build 'src/MassiveSlicer.App/MassiveSlicer.App.csproj' -c Release
if ($LASTEXITCODE -eq 0) {
    Start-Process -FilePath '.\src\MassiveSlicer.App\bin\Release\net8.0-windows\MassiveSlicer.App.exe' `
      -WorkingDirectory '.\src\MassiveSlicer.App\bin\Release\net8.0-windows'
}
```

Publish variant (optional): `scripts\publish-and-run.ps1` → `%LOCALAPPDATA%\MassiveSlicer\build`.

### Start only (no rebuild)

```powershell
Start-Process -FilePath 'Z:\Research\LFAM\MassiveSLICER\src\MassiveSlicer.App\bin\Release\net8.0-windows\MassiveSlicer.App.exe' `
  -WorkingDirectory 'Z:\Research\LFAM\MassiveSLICER\src\MassiveSlicer.App\bin\Release\net8.0-windows'
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

#### LFAM 3 phase switch: mount tool, do not select TCP
- `SelectLfam3WorkflowPhase` updates `_lfam3WorkflowPhaseIndex` and mounts Extruder / Scanner (Calibrated) / Spindle (No Bit).
- `SuppressNextToolViewportSelect` skips `_renderer.Select` so the gizmo does not jump to the TCP.

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

### 2026-08-21 — Auto shop wipe when a slice has travel moves

- After realtime / first slice, if the toolpath has any `MoveKind.Travel`, Wipe is set to Same-Direction, 35 mm, 5 mm ramp, 600 mm/s.
- Once per source mesh so a later Retrace / custom length is kept. Wipe + Z hop now trigger a realtime reslice.
- Key files: `Toolpath.cs`, `AdditiveSettingsViewModel.cs`, `ViewportView.axaml.cs`.

### 2026-08-21 — Dialog windows: no black square behind rounded corners

- Symptom: Material Preset (and other borderless dialogs) showed a black 90° HWND behind the 10px card. `TransparencyLevelHint` is ignored because the shop Windows build forces WGL (no per-pixel alpha).
- Fix: `DialogWindowChrome` clips the Win32 window to the DialogChrome radius (`SetWindowRgn` + Win11 DWM round). Those corner pixels are no longer part of the window. Also `CornerClip` on `Border.DialogChrome`.
- Key files: `DialogWindowChrome.cs`, `Resources/Styles/BaseStyles.axaml`.

### 2026-08-21 — Expanded StepCards keep rounded corners

- Symptom: TOOLPATH (and other expanded cards) looked square. Avalonia ClipToBounds is a rectangle, so GLOBAL / header fills painted over the 5px CornerRadius.
- Fix: `CornerClip` sets Visual.Clip to a rounded rect on StepCard / SectionExpander / PresetsCard shells. Header ToggleButtons use a chrome-less template (radius 5 collapsed, 5,5,0,0 expanded).
- Key files: `Behaviors/CornerClip.cs`, `Resources/Styles/BaseStyles.axaml`.

### 2026-08-21 — Adaptive Speed card matches ADVANCED SETTINGS width

- The Adaptive Speed and Flow card sat inset (8px Border margin plus SectionExpander body padding).
- ADVANCED SETTINGS now uses TightPad; the card is stretch with no left/right margin so it matches the expander header.
- Key file: `RightPanelView.axaml`.

### 2026-08-21 — WIPE sits below MOVEMENT

- Wipe was a nested expander inside MOVEMENT. It is now its own SectionExpander immediately under MOVEMENT (before STOCK FROM MAPS). Bindings unchanged.
- Key file: `RightPanelView.axaml`.

### 2026-08-21 — KRL Post-Processing factory JSON keeps Rules

- Symptom: changing Rules (Travel Moves, Code Editor inject, air, timing) and recompiling reset to in-code defaults. Done only wrote header/footer, and wrote the **bin/** copy that a rebuild wipes.
- Fix: Done writes the full recipe to repo `assets/krl_postprocess.json` (plus the bin copy). Startup applies that file after prefs so GitHub / rebuild keep the same defaults.
- Key files: `KrlPostProcessLoader.cs`, `KrlPostProcessSettings.cs`, `AssetPaths.cs`, `KrlPostProcessSettingsViewModel.cs`.

### 2026-08-21 — Code Editor inject: print speed + Before/After

- Removed the Speed (mm/s) field. Time offsets always use the job Print Speed.
- Stop Extruding `$VEL.CP` is rewritten to 50% of print speed (UI updates when print speed changes; export does the same).
- Stop / Enter URM / Exit URM each have a Before/After dropdown. Defaults: Stop Before, Enter Before, Exit After.
- Key files: `CodeEditorInjectSettings.cs`, `CodeEditorSrcInjector.cs`, `KrlPostProcessWindow.axaml`.

### 2026-08-21 — Start/stop timing left MOVEMENT

- Start wait, pre-travel pause, pre-resume pause, resume prime, and post-travel ramp are gone from the right-sidebar MOVEMENT section.
- Same fields now sit under **KRL Post-Processing → Travel Moves** (shown when Start/Stop is on), below Code Editor inject. Bindings still on Additive; prefs / .mass / export unchanged.
- Key files: `RightPanelView.axaml`, `KrlPostProcessWindow.axaml`, `KrlPostProcessSettingsViewModel.cs`.

### 2026-08-21 — Live I/O: ~50% denser

- Halved row height (30 → 18), fonts (11 → 9 / 8), column cards, gutters, and
  min-widths (dual 680 → 380, robot 860 → 500). Table columns kept so
  `AI-3020` still has a reserved pin slot.

### 2026-08-21 — Orientation smoothing moved to KRL Rules

- Smooth rotation, max rate, KRL look-ahead, and KRL sigma left the right-panel toolpath colors section.
- Same fields now live under **KRL Post-Processing → Rules** (after $APO.CVEL). Radius (moves) still appears only when Smooth rotation is on. Bindings still sit on Additive; prefs / .mass / export unchanged.
- Key files: `KrlPostProcessWindow.axaml`, `KrlPostProcessSettingsViewModel.cs`, `RightPanelView.axaml`.

### 2026-08-21 — Extruder Air ($OUT[5]) checkbox

- KRL Post-Processing Rules: **Extruder Air** checkbox. On = `$OUT[5] = TRUE` after the header, `$OUT[5] = FALSE` before footer `END`. Off = no extra I/O. Robot Mode footer already turns air off — we do not duplicate.
- Print only (mill ignored). Comments are `;extruder air on/off` (no `$OUT` token). Persists in prefs / `.mass`. Console: `krlpost air on|off`.
- Key files: `KrlExporter.cs`, `KrlPostProcessWindow.axaml`, `AdditiveSettingsViewModel.cs`, `AppPreferences.cs`.

### 2026-08-21 — Travel Moves + wipe on by default

- Symptom: new jobs started with Travel Moves off and Wipe Off (or 12 mm / 4 mm ramp).
- Fix: factory defaults are Travel Moves (start/stop) ON, Wipe Same-Direction, 35 mm, 600 mm/s, ramp = layer height + 2 mm. Ramp tracks layer height until you type a different value. Robot Mode stays off. Prefs migrate the old Off / 10 mm / 120 mm/s factory once.
- Key files: `AdditiveSettingsViewModel.cs`, `AppPreferences.cs`, `PreferencesLoader.cs`, `RightPanelView.axaml`.

### 2026-08-21 — Code Editor inject in KRL Travel Moves

- Symptom: Travel Moves only wrote `;layer change` / pulsed I/O at the last print pose. Wipe was not a travel. Cow Capital had 0 `;travel start`.
- Cause: exporter used the old Caracol PreTravel/PostTravel block and treated layer-change as a different tag.
- Fix: Code Editor 1.0.6 recipe under **KRL Post-Processing → Travel Moves**. Exporter always writes `;travel start` / `;travel end`; wipe opens travel. After export, `CodeEditorSrcInjector` inserts start/stop/URM at path distances. PointLoader-safe I/O is on by default (no `TRIGGER` in the CAD body).
- Key files: `CodeEditorInjectSettings.cs`, `CodeEditorSrcInjector.cs`, `KrlExporter.cs`, `KrlPostProcessWindow.axaml`, `KrlPostProcessSettingsViewModel.cs`, `AdditiveSettingsViewModel.cs`.
- Branch: `feature/code-editor-inject` (tree was 7 behind origin/main + dirty; did not pull).

### 2026-08-21 — Live I/O columns: readable table layout

- Signal rows were 34px pin + collapsing analog status, so `AI-3020` ran into
  the label and values had no aligned column.
- Each row is now pin (58, mono) · reserved status · label · value (76, right)
  · action. Analog keeps the status slot empty instead of collapsing.
- INPUTS / OUTPUTS sit in padded column cards with 20px gutter. Row padding
  8,6 / min-height 30. Section min-width 680 (dual) / 860 (robot).

### 2026-08-21 — KRL Post-Processing Rules: Robot Mode vs Travel Moves

- Replaced the single Digital Start/Stop checkbox with two always-enabled toggle
  buttons. **Robot Mode** writes temps + RPM (`T1/T2/T3/RPM` MAT). **Travel Moves**
  writes start/stop around travels. Enabling Travel shows a placeholder for
  upcoming customization.
- Exporter splits `UseRobotMode` / `UseTravelStartStop`. Legacy
  `DigitalStartStopEnabled=true` still turns both on (existing tests + old
  workspaces). New workspaces persist `RobotModeEnabled` separately.
- Console: `krlpost robot on|off`, `krlpost travel on|off` (`urm` still toggles both).

### 2026-08-21 — PRESETS: 2px left/right content inset

- SectionExpander body was 4 + ContentPresenter 6 + stack 8. Now PRESETS uses
  `TightPad`: ExpandedBody `2,0,2,2`, content presenter 0, stack no extra L/R.

### 2026-08-21 — Purple TOOL# ring: ControlTheme, not more Style setters

- Root cause: Avalonia ControlTheme beats Style Template. Previous "full templates"
  in BaseStyles never applied. SimpleTheme ComboBox + ToggleButton painted
  ThemeBorderHigh (~`#642d55`).
- New `Resources/Themes/ControlThemes.axaml` (`MassiveComboBoxTheme` + chrome-less
  toggle). BaseStyles now sets `Theme="{StaticResource MassiveComboBoxTheme}"`.
- Focus/open border hard-coded `#71a72a`. ThemeBorderHigh also forced lime at
  Application.Resources (non-variant).


### 2026-08-21 — Hover field border: purple → lime `#71a72a`

- Symptom: hovering TextBox / ComboBox / NumericUpDown showed a purple ring.
- Cause: SimpleTheme ControlTheme sets `Border#border` to `ThemeBorderHighBrush` on pointerover (style setters cannot beat it). Custom ComboBox template still used `Name="border"`, so the purple rule still hit. `ApplyTheme` also remapped ThemeBorderHigh to `Border2` (Cosmic `#2f2550`).
- Fix: TextBox + ComboBox chrome renamed `fieldChrome`; hover/focus hard `#71a72a`. ThemeBorderHigh = Accent (not Border2).
- Files: `BaseStyles.axaml`, `App.axaml`, `App.axaml.cs`.

### 2026-08-21 — Purple ComboBox ring removed + dialog corners round for real

- Sampled TOOL# ring approx `#642d55` (OS/SimpleTheme purple) — not MassiveMAKE lime.
- Full ComboBox/ComboBoxItem ControlTemplate in BaseStyles; focus border hard `#71a72a`.
- DialogWindowChrome forces transparent HWND so Preferences CornerRadius shows.
- Theme colors: `Resources/Themes/MassiveMake.axaml`, `App.axaml` ThemeDictionaries, `BaseStyles.axaml`.


### 2026-08-21 — ComboBox flyout: rounded corners actually show

- SLICING METHOD (and all) dropdowns: 10px popup + square selected fill
  sat on the 5px field; lime `/template/ Border` also painted inner
  presenters and squared the corners.
- Flyout is now `Popup > Border` only: **5px**, 3px pad, ClipToBounds.
- Selected / hover fill is on the item ContentPresenter (5px), not the
  full-bleed item. File: `Resources/Styles/BaseStyles.axaml`.

### 2026-08-21 — PRESETS Sort/Group compact icon toolbar

- Replaced text Sort/Group rows with one icon toolbar:
  Sort A–Z / last printed / created · Group list / method / folder · ★ favorites.
- List rows simplified to name + star + info (badge/date moved to tooltip/flyout).
- Dropped verbose help blurb; search watermark shortened.

### 2026-08-21 — ComboBox focus/open border: purple → lime

- MATERIAL (and all) ComboBox focus / dropdown-open ring was OS/SimpleTheme purple.
- Force `#71a72a` on `:focus`, `:focus-visible`, `:dropdownopen`, `:pressed`, plus nested Borders.
- App.axaml SystemAccentColor* + SystemControlHighlight* keys set to lime; theme swap keeps them in sync.

### 2026-08-21 — Dialog + popup windows: 10px rounded corners

- All borderless dialogs (Material Preset, Preferences, Mill Tool library,
  Cut Tool, Mesh Cleanup, KRL Post-Process): transparent window + root
  `DialogChrome` Border CornerRadius 10 + ClipToBounds; title top / footer bottom radii.
- BaseStyles: ContextMenu / Menu / ComboBox popup / Flyout / ToolTip radius 10 (or 8).
- MassiveBOARD: `--radius: 10px`, `.modal { overflow: hidden }`.

### 2026-08-21 — Lime accent #71a72a (kill purple #523446 / #412335)

- MassiveMake + Cosmic themes: Accent `#71a72a`, Hover `#8bc43a`, Muted `#3a5716`.
- Cosmic no longer violet (was Accent `#c054f0` / muted `#3d1060` / purple Bgs).
- App.axaml SimpleTheme Highlight / ThemeAccent keys synced to lime.
- BaseStyles hardcoded `#40b840` family → `#71a72a`; expanded section chrome
  uses muted lime glass (not gray that read purple).
- MassiveBOARD `style.css` / robots / ai-brand same lime.

### 2026-08-21 — SectionExpander: expanded-body hairline matches header

- Open section body (e.g. FILL PATTERN → Pattern/None) gets a 1px border in
  the expanded header fill `#E62e2e2e` — same gray as the FILL PATTERN bar.
- Bottom corners 5px; header top-only when open. File: `BaseStyles.axaml`.

### 2026-08-21 — StepCard uniform translucency (OUTLINER was solid)

- Expanded StepCard body was a second `#E6171717` layer — stacked with the
  shell and made OUTLINER / filled cards look opaque while sparse cards
  (ROBOT CELL) still ghosted.
- Body is now transparent; one shell only `#E61e1e1e` (~90%) for every card.
- Import dropzone uses `GlassInset` (`#E62b2b2b`) instead of solid Bg3.
- SectionExpander body transparent too. File: `BaseStyles.axaml`.

### 2026-08-21 — Live I/O: KUKA Analog O1-O4 editable + Zero All

- Robot (KUKA) outputs show **Analog O1–O4** (`$ANOUT[1..4]`) on every cell.
- Each row: engineering edit box (O1–O3 °C, O4 % RPM) + **Set** via C3Bridge.
- **Zero O1-O4** on OUTPUTS header writes all four to 0.
- `TryWriteKukaRealAsync` on RobotPanelViewModel. Catalog Writable=true.

### 2026-08-21 — SectionExpander: no border, even spacing

- Nested rows (SLICING METHOD / MATERIAL / …) drop the stroke — fill color only.
- Default margin `6,4,6,4` (same feel as STOCK FROM MAPS); per-row Margin overrides cleared.
- Hover/expand is a lighter fill, not a border. File: `BaseStyles.axaml`.

### 2026-08-21 — Sidebar compact: 5px corners, tighter spacing, 90% opacity

- StepCard / SectionExpander / inputs / combos: CornerRadius **5** only.
- Less pad/margin (headers 8px, card gap 6, nested section 4/2).
- Dark panel fills use **#E6…** (~90% alpha) so the viewport shows through.
- Soft box-shadows removed. MassiveLAB cards match. File: `BaseStyles.axaml`.

### 2026-08-21 — MassiveLAB left menu (ERP out of viewport dock)

- Removed bottom-left ERP overlay dock from ViewportOverlayView.
- New left StepCard **MASSIVELAB** between **ROBOT** and **VIEWPORT**.
- ErpDockPanelView redesigned: connection strip, card search results, linked
  workspace banner, Send Slice CTA, pricing footer (glass kit).
- Header badge: Offline / Online / project·element. Files: LeftPanelView,
  ErpDockPanelView, ErpViewModel, ViewportOverlayView.

### 2026-08-21 — Sidebar: stop scrolling into blank space

- `SidebarExpandScroll` no longer injects a permanent ~viewport-tall pad.
- Pad = only what the expanded StepCard needs to pin to the top (`cardY +
  viewport - realContent`). Zero when content already fills the column.
- Collapse shrinks/removes pad and clamps Offset so you cannot sit in dead space.

### 2026-08-21 — Glass kit sidebar (match reference UI sheet)

- StepCard / SectionExpander: elevated dark glass cards (14/12px radius, soft
  shadow), clearer body inset so collapsibles read as menus not flat rows.
- PanelTextBox + ComboBox: pill fields (12px), lime hover/focus ring.
- Slider: thick recessed track, lime fill, soft glowing round thumb.
- ToggleSwitch: dark track off / lime-on track + knob (default Avalonia parts).
- CheckBox: darker well, lime when checked. File: `BaseStyles.axaml`.

### 2026-08-21 — Sidebar collapsibles: rounded cards + better inputs/sliders

- SectionExpander (SLICING METHOD / GEOMETRY / SEAM / …) is a nested rounded
  mini-card (Bg2, 7px radius) with hover/expanded header, not a flat list row.
- StepCard shell darker (#1f) so nested sections pop; content body separated.
- PanelTextBox: 6px radius, taller padding, lime hover/focus border.
- Panel Slider: lime filled track + round thumb (not default Avalonia chrome).
- ComboBox: taller + 6px radius. File: `Resources/Styles/BaseStyles.axaml`.

### 2026-08-21 — Drive Cell 3D rebuilt as MassiveSLICER scene

- three.js now parents like `CellSceneLoader` / `CellEnvironmentBuilder`:
  GltfToScene (Rx+90 x1000), robot translate-only, rotary = ROBROOT+basePos then
  world-Z yaw then KUKA ABC. Lime marker at Print Bed 0,0,0 (916.31).
- Display adapter `kuka-root` Rx(-90)x0.001 unchanged. `?v=slicerscene1`.

### 2026-08-21 — Drive Cell 3D bed sat 1 m below Slicer print plane

- Slicer on-bed first layer is file/SRC Z ~3 and $POS_ACT Z ~919 (bed 916 + 3).
- Copying Slicer `basePos` Z −655 / C −90 *without* ROBROOT + Y-up wrap dumped the
  deck on its side (screenshot). Slicer world = ROBROOT + basePos (z 345) then C −90.
- Drive `cell-view.js` now `wrapYupGlb` + ROBROOT + basePos, same as
  `CellEnvironmentBuilder`. Motion still plays Slicer XYZ + bed_origin.

### 2026-08-21 — Drive rebaselines from Slicer only

- Rule: MassiveSLICER SRC/package is the path. Drive does not invent XYZ or
  slide a Slicer job onto live TCP / Home. `bed_origin` is applied once in
  memory; saved file stays BASE (Z ~3–5). Double-expand and hardcoded LFAM 3
  origin removed.
- Files (Drive): `job_package.py`. Slicer exporter already matches SRC.

### 2026-08-21 — Drive job file Z 919 vs SRC Z 3

- SRC / slicer TCP ACTUAL is print-bed BASE (first layer Z ~3). Send was writing $POS_ACT
  world (Z ~919). That is the same point (bed 916 + 3 mm) but the saved JSON looked 900 mm high.
- File now matches SRC: WorldToBase + `meta.frame=base` + `meta.bed_origin`. Drive adds origin
  only in memory (validate / RSI / Cell 3D). Does not rewrite the saved file to world.
- Files: `MassiveDriveJobExporter.cs`, `ViewportView.axaml.cs`; Drive `job_package.py`,
  `path_validation.py`, `path_executor.py`.

### 2026-08-21 — Drive V1 rejected Six squares bed-local XYZ (Z 3)

- Latest job `2dc0d1089302` (Send 15:02 UTC): first pose `-180, -110, 3`. Drive V1 bounds are
  $POS_ACT world (x 800-3200, z 50-2200) so 726 out_of_bounds and 425 IK soft-limit.
- Slicer TCP ACTUAL (BASE 1) is bed-local. KUKA BASE_DATA[1] is {X 0, Y 0, Z 5}; RSI is world.
  Correct first print is about `1955, -163, 919` A0 B90 C0. Keep meta.absolute + frames T1/B1.
- Revert WorldToBase on Drive export. Files: `MassiveDriveJobExporter.cs`, `ViewportView.axaml.cs`.

### 2026-08-21 — T1 triad sat on flange TCP instead of TOOL_DATA[1]

- Symptom: MassiveSLICER viewport (not Drive Cell 3D). PRINT / TOOL #1 drew the Tool
  Triad at A6. Cause: T1 is the cell default, so remount skipped and `_tcpOffsetLocal`
  stayed 0. Mill cutter snap could also steal T1 if a spindle mesh was still current.
- Fix: always apply TOOL_DATA on TOOL # / PRINT even when the combo index is already 0.
  T1/T4/T5/T6 keep taught XYZ/ABC. Spindle mill tools still snap to SpindleBitTCP.
  Readout fallback pulls cell TCP if offset is still zero. JSON not rewritten.
- Files: `RobotPanelViewModel.cs`, `ViewportViewModel.cs`, `ViewportView.axaml.cs`.

### 2026-08-21 — Send to MassiveDRIVE still targeted old brain .233

- Cell JSON `massiveDriveUrl` was `http://192.168.0.233:8080` (RevPi, off). Live Drive is `http://192.168.0.201:8080`.
- Updated all three `lfam3.json` copies. Cal unreachable message no longer says "on 233".
- Files: `assets/cells/LFAM3/lfam3.json`, `src/assets/cells/LFAM3/lfam3.json`, `src/MassiveSlicer.App/Assets/cells/LFAM3/lfam3.json`, `MainWindowViewModel.cs`.

### 2026-08-20 — Triad axis colors locked (RGB = XYZ)

- Canonical: **X red · Y green · Z blue** (robotics/CAD).
- Documented in header section **Convention — triad / gizmo axis colors**.
- Code already agrees: `AxisRenderer`, `TcpAxisLabelLayout`, `GizmoRenderer`.
- False friends: green cutter cylinder ≠ Y; dual TCP/FLANGE triads share colors but different frames.
- Agent rule: treat any X≠red / Z≠blue paint as a bug, not a new convention.

### 2026-08-18 — Control bridge can listen on shop LAN (not Lab MCP)

- App already starts `LocalControlBridge` on launch (`127.0.0.1:8723`). MCP shim already at `scripts/mcp/massiveslicer_mcp.py`. This is **not** MassiveLAB.
- LAN opt-in: `%LOCALAPPDATA%\MassiveSlicer\bridge.lan` = `1` or `MASSIVESLICER_BRIDGE_LAN=1`. Default stays loopback (`POST /command` can move the robot).
- `/status` now includes `workspace` + `lan` + `port`.
- MCP shim: `MASSIVESLICER_BRIDGE_HOST` (SB101 `192.168.0.69`) + macOS `~/Library/Application Support/MassiveSlicer/bridge.port`.
- Do not register Hermes MCP until `curl http://192.168.0.69:8723/ping` works.

### 2026-08-18 — Edit Point mode shows programmed corners

- Symptom: lime edit points sat mid-side; square corners were empty. Cause: Point mode drew **bead midpoints** (`(From+To)/2`). Long wall moves (one LIN per side, or 3 pts/side) put the sphere in the middle, never at the vertex.
- Fix: `ToolpathEditPoints` emits **From of each extrude + To of the last in a run** (closed loops drop the duplicate close). Renderer, hover, box-select use those vertices.
- Files: `ToolpathEditPoints.cs`, `ToolpathRenderer.cs`, `ViewportView.axaml.cs`, `ToolpathEditPointsTest.cs`.

### 2026-08-17 — Mill Send is absolute (Drive syncs live TCP, then lead-in)

- Mill packages stamp `meta.absolute` + approach clearance. Drive does **not** rebase the mill raster onto live TCP.
- On Run, Drive reads RSI pose and prepends lift / XY / plunge from here to the first mill point. Cut XYZ stay in cell/base.
- Air-cuts (`meta.air_cut`) still rebase. Files: `MassiveDriveJobExporter.cs`, `ViewportView.axaml.cs`. Drive: `job_package.prepend_sync_lead_in`, `path_executor.begin`.

### 2026-08-17 — Send mill path to MassiveDRIVE (upload, no auto-start)

- Mill Send now emits `kind=mill` + mill ABC (`AbcFromMillNormal`) + `frames.tool` (T12) + `meta.spindle_rpm`.
- Drive rejects mill packages at RPM 0. Slicer blocks send until MILL TOOLPATHING Spindle RPM is set.
- Mill Send **uploads only**. Run from Drive Jobs after preflight. Path rebases to live TCP — jog to first cut first.
- Files: `MassiveDriveJobExporter.cs`, `ViewportView.axaml.cs`, `MassiveDriveJobExporterTest.cs`.

### 2026-08-17 — Mill TOOLPATHING Offset Distance (+/− along surface)

- PASSES, above Number of depth cuts: **Offset Distance** (mm). + pushes the cutter out along the surface normal; − cuts into the work. Default 0. Range −200…+200.
- Independent of Stock to leave. Generate / realtime mill apply it in `SurfaceFollowMillGenerator`.
- Saved on `.mass` (`MillSidebarSettings.OffsetDistanceMm`). Console: `mill offset <mm>`.
- Files: `RightPanelView.axaml`, `SubtractiveSettingsViewModel.cs`, `MillSettings.cs`, `MillSidebarSettings.cs`, `SurfaceFollowMillGenerator.cs`, `ViewportView.axaml.cs`.

### 2026-08-17 — .mass reopens MILL workflow and keeps TOOL #12

- Reopen snapped to PRINT: UiSession never stored LFAM 3 phase. Cell load reset the timeline to Print; mesh/toolpath select then forced Additive.
- Clicking MILL then mounted **Spindle (No Bit) = T2**, wiping saved T12.
- Save `Lfam3WorkflowPhase` / `MountedToolName` / pre-print flag. Restore MILL before models. MILL button keeps T12 / Face Mill / spindle TOOL #; T2 only if the live tool is not a mill.
- T12 / spindle.glb names count as MILL (not only "Spindle (No Bit)").
- Files: `WorkspaceDocument.cs`, `WorkspaceService.cs`, `ViewportViewModel.cs`, `MainWindowViewModel.cs`, `ViewportView.axaml.cs`, `WorkspaceSaveTest.cs`.

### 2026-08-17 — Mill TOOLPATHING has its own Milling + Travel speeds

- 600 mm/s on mill playback / KRL rapids was **print** Additive.TravelSpeed.
- TOOLPATHING → MOVEMENT: **Milling speed** (CuttingFeedMmS) + **Travel speed** (TravelSpeedMmS, default 80 mm/s). Not print MOTION.
- HUD, estimate, KRL `$VEL.CP` rapids, and Drive mill packages use these. Generate stamps TravelSpeedMps on mill travels.
- Console: `mill speed <mm/s>`, `mill travel <mm/s>`.
- Files: `RightPanelView.axaml`, `SubtractiveSettingsViewModel.cs`, `MillSidebarSettings.cs`, `ViewportView.axaml.cs`, `KrlExporter` wiring, `MassiveDriveJobExporter.cs`.

### 2026-08-17 — Mill IK orients T12 ABC, not the flange

- Planar facing looked aligned to the flange triad. T12 is taught at A=103.7 B=-43.7 C=40.5 vs flange.
- Cause: orientation IK matched joint_6 / flange rows. Position already used SpindleBitTCP.
- Fix: when a spindle cutter is mounted, solver applies **T12 TOOL_DATA ABC only** (no T12 XYZ). Triad location stays on SpindleBitTCP.
- Files: `GltfNumericalIkSolver.cs`, `ViewportView.axaml.cs`.

### 2026-08-17 — Lab mill-tools API is live; slicer client matches

- Probed lab.massivemake.com with a Slicer Access token: GET /mill-tools 200 `{items, version}` (4 bits), presets-bundle includes millTools, POST 201, PUT 200, DELETE 204. PATCH is 404 (CORS lists it; slicer already uses PUT).
- Local mill_tools.json already has matching ErpIds (mt_…) — connect sync works.
- Hardened MillBitTool: do not serialize TypeDisplayName / IsBallEnd / DefaultPreset; recover CuttingPresets if Lab only sent DefaultPreset; Type 0/1 still deserializes.
- Tests: ParsesLiveLabMillToolsListEnvelope, MillBit_recovers_CuttingPresets_from_DefaultPreset.
- Files: `MillBitTool.cs`, `ErpParsingTest.cs`, `docs/ERP-SlicerAPI.md`.

### 2026-08-17 — .mass did not restore mill right-sidebar settings

- Symptom: Planar Facing, Box Selection, feeds, bit, planar axis all reset on reopen.
- Cause: `AppPreferences` had no mill block. PersistSettings / CopyPreferences / SyncViewportFromPrefs only covered additive + scan.
- Fix: `MillSidebarSettings` on prefs + .mass. Capture/apply on Subtractive. Old files with null Mill leave the live panel alone.
- Files: `MillSidebarSettings.cs`, `AppPreferences.cs`, `SubtractiveSettingsViewModel.cs`, `MainWindowViewModel.cs`, `WorkspaceSaveTest.cs`.

### 2026-08-17 — Mill TOOLHEAD GLOBAL ORIENTATION (same Y/X/Z as print)

- Mill TOOLPATHING → MOVEMENT now has the same Y / X / Z sliders as print. Separate from print Additive.Toolhead*.
- Applied as local KUKA ZYX after mill ABC (T12 +Z into the surface). Viewport IK, validation, and mill KRL use Subtractive.ToolheadA/B/C.
- Console: `mill y <deg>`, `mill x <deg>`, `mill z <deg>`.
- Files: `RightPanelView.axaml`, `SubtractiveSettingsViewModel.cs`, `KukaOrientation.cs`, `GltfNumericalIkSolver.cs`, `KrlExporter.cs`, `ViewportView.axaml.cs`.

### 2026-08-17 — .mass did not restore ROBOT CELL TOOL # / BASE #

- Symptom: reopen a saved workspace and the left TOOL # / BASE # pickers snap back to the cell default (LFAM 3 = T1 / first base), not what was selected at save.
- Cause: UiSession stored joints but not the KRL frame pickers. Cell swap called `SetKrlFrameOptions` with Scan indices and always preferred the cell default tool. Restore then copied that default back onto Additive via `SyncKrlFrameIndicesToActiveTab`. PersistSettings wrote Additive.ToolDataIndex, which is stale on MILL.
- Fix: save `KrlToolIndex` / `KrlBaseIndex` on UiSession; persist live robot pickers into Settings; restore after cell ready (`SelectKrlFrames`); honor the saved/current tool on cell swap instead of forcing the default.
- Files: `WorkspaceDocument.cs`, `WorkspaceService.cs`, `RobotPanelViewModel.cs`, `MainWindowViewModel.cs`, `WorkspaceSaveTest.cs`.

### 2026-08-17 — Cutting Tool Library syncs with Lab ERP (slicer ready; Lab route pending)

- Same pattern as print/material presets. `MillBitTool.ErpId`; connect pulls `/mill-tools` (or `presets-bundle.millTools`) and merges by ErpId then desktop Id then Name; local-only rows POST.
- Save mill library / persist bit: POST or PUT. Delete from the dialog DELETEs the ERP row.
- 404 = Lab not shipped yet (local `mill_tools.json` only). Console: `erp millbits`.
- Lab prompt: `docs/ERP-MillTools-API-Replit-Prompt.md`.
- Files: `ErpClient.cs`, `ErpPresetSync.cs`, `ErpModels.cs`, `ErpViewModel.cs`, `SubtractiveSettingsViewModel.cs`, `MillBitTool.cs`, `MillBitLibraryLoader.cs`, `MillBitLibraryViewModel.cs`, `MainWindowViewModel.cs`. Test: `ParsesMillToolsInPresetsBundle`.

### 2026-08-17 — Mill IK follows the cutter, triad stays on the mesh

- Visible TCP / green bit stay on SpindleBitTCP. Do not load T12 TOOL_DATA onto the triad.
- Spindle (No Bit) taught TCP is still extruder CRE_HV, so IK used to aim that point at the path and the cutter sat off the beads.
- RebuildIkSolver now uses the flange-local offset of `TryGetCutterWorld` when a spindle is mounted. Same point the triad draws. TOOL_DATA fields unchanged.
- File: `ViewportView.axaml.cs`.

### 2026-08-17 — Mill TCP triad back on the spindle mesh

- Putting T12 TOOL_DATA on the triad (and skipping SpindleBitTCP during playback) moved the TCP off the mesh. Mesh/TCP were already aligned; only the path follow was wrong.
- Reverted: triad always uses `TryGetCutterWorld` (SpindleBitTCP). Generate no longer swaps in T12 offsets / SelectToolByKrlIndex(12).
- File: `ViewportView.axaml.cs`.

### 2026-08-21 — Sidebar StepCard expand crashed the app ("Infinite layout loop detected")

- **Symptom:** clicking **Pattern/Effects** or **Toolpath** in the right panel aborted the
  process (SIGABRT). The macOS crash report showed only native Avalonia/Skia frames; the
  managed exception was `InvalidOperationException: Infinite layout loop detected` thrown from
  `MediaContext.FireInvokeOnRenderCallbacks`.
- **Cause:** `SidebarExpandScroll.Schedule` subscribed `sv.LayoutUpdated` to a handler calling
  `TryPinToTop`, which writes `sv.Offset` and resizes the scroll pad — so *every* call dirtied
  layout and re-raised `LayoutUpdated` inside the **same** render callback. The retry counter
  `attempts` was only advanced by `Tick` (dispatcher-driven), never by the layout handler, and
  the handler's only exit was `TryPinToTop` returning true (`|origin.Y| < 3`) — unreachable for
  a card whose content grew such that it can no longer reach the top. Unbounded re-entry inside
  one layout pass, so Avalonia aborted the process.
- **Fix (this branch):** do not hook `LayoutUpdated` at all — background dispatcher retries only.
  Main's `MaxLayoutPasses` cap is superseded by that (same crash, stronger fix).
- **Diagnostics:** `Program.cs` now adds Trace listeners into the crash log and stderr.
- **Files:** `src/MassiveSlicer.App/Behaviors/SidebarExpandScroll.cs`,
  `src/MassiveSlicer.App/Program.cs`.

### 2026-08-21 — `main` was unbuildable Aug 17–21; fixed upstream. Joint-limit envelope still open

- `2181741` (Aug 17) committed the new `ViewportView.axaml.cs` referencing `OutlinerToolpathKind`
  et al. without the files defining them — a clean clone failed at `ViewportView.axaml.cs(13922)`
  with CS0246. Nick and Jeff fell back to build 573 as a baseline for four days.
- Fixed upstream by `336e69d` (Aug 21, MassiveMAKE), which restored **8 files / 233 lines**.
- **Root-cause pattern to avoid:** `git commit -a` stages tracked-file edits but silently skips
  untracked files. Use `git add -A`, and build a clean worktree before pushing.
- **STILL OPEN — robot motion.** `JointLimitEnvelope.MarginFraction = 0.05f` insets 5% of each
  axis travel. Not applied here.

### 2026-08-16 — macOS Release build: NETSDK1177 on SMB apphost

- `codesign` failed on `obj/Release/net8.0/apphost` with "resource fork, Finder information, or similar detritus not allowed" because MassiveFILES is SMB.
- Fix: `<_EnableMacOSCodeSign>false</_EnableMacOSCodeSign>` on OSX in `MassiveSlicer.App.csproj`. `dotnet run` does not need an ad-hoc signed host.
- File: `src/MassiveSlicer.App/MassiveSlicer.App.csproj`.

### 2026-08-16 — Mill T12 TCP now rides the facing path

- MILL workflow mounts Spindle (No Bit), whose JSON TCP is still the extruder CRE_HV numbers. IK aimed that point at the path; the triad/bit were elsewhere.
- Generate now applies taught T12 TOOL_DATA to IK, then arms scrub and snaps to the first mill move.
- While a mill path is armed, the TCP triad is flange+TOOL_DATA (same point IK solves), not the SpindleBitTCP mesh datum.
- File: `ViewportView.axaml.cs`.

### 2026-08-16 — Planar facing TOOL AXIS (not locked to world -Z)

- Planar Facing / Planar Clearing used to raster world XY and take the topmost Z. T12 stayed vertical.
- New OPERATION → TOOL AXIS: World ±X/Y/Z, Painted area, Camera view, Custom XYZ, plus Tilt / Azimuth.
- Generate rasters in that frame and locks move normals so mill ABC / T12 +Z = −approach.
- From painted area / From camera buttons. Console: `mill axis`, `mill tilt`, `mill azimuth`.
- Files: `MillPlanarOrientation.cs`, `SurfaceFollowMillGenerator.cs`, `SubtractiveSettingsViewModel.cs`, `RightPanelView.axaml`, `ViewportView.axaml.cs`. Tests: `MillPlanarOrientationTest`.

### 2026-08-16 — Mill Box/Lasso only paint the front surface

- Region select used every vertex inside the screen rectangle, including the back of the part.
- Now: front-facing triangles only, plus a coarse depth buffer so the far wall is not painted through. Alt-erase uses the same rule. Lasso shares the path.
- Files: `MillFrontSurfaceBox.cs`, `MillSurfacePaint.cs`, `ViewportView.axaml.cs`. Tests: `MillFrontSurfaceBoxTest` 2 passed.

### 2026-08-16 — LFAM 3 circular bed grid was buried under the rotary platter

- Polar overlay (circle + 500 mm rings + 30 deg spokes + origin) still built from `bed.diameter` 1828.8. It was drawn *before* meshes at the same Z as `rotary_bed_top.glb`, so Bed Grid looked off.
- Draw after meshes with depth test off. Lift 2 mm. Grid colour brighter lime.
- Files: `SceneRenderer.cs`, `BedBoundaryRenderer.cs`.

### 2026-08-16 — Mill Box select started a panel-width to the right of the cursor

- Overlay chrome (`OverlayRoot`) is inset `320,52,320,0` so tools clear the side cards. The box/lasso canvases lived inside that inset; pointer + `ProjectToScreen` are full-viewport.
- Fix: draw marquee/lasso on the outer overlay grid (same as TCP x/y/z tags). Paint-mode box select uses the same canvases.
- File: `ViewportOverlayView.axaml`.

### 2026-08-16 — Connections could not scroll to LFAM 3 user/pass

- Keep-on-bed stayed visible on every Preferences page and ate the top of the scroller. Hidden unless Navigation.
- Last SMB card (LFAM 3) had no bottom pad, so username/password sat under the Done bar. +120 px pad; Floating scroller with a visible bar.
- Files: `PreferencesWindow.axaml`, `PreferencesWindow.axaml.cs`.

### 2026-08-16 — Preferences window 60% wider; Connections page recast

- Window 620x640 -> 992x700 (min 832). Nav column 188. No dingbat labels.
- Connections: Lab ERP card (email/password row, URL, projects root, Sign in + status pill). API token tucked under Advanced expander.
- Robot SMB: 3-col cards (IP / share / folder, then user / password / Test).
- Files: `PreferencesWindow.axaml`, `ErpViewModel.IsConnecting`.

### 2026-08-16 — Green spindle cylinder vanished with the TCP plane

- TCP at T12 was correct; "Show cylinder on spindle" drew nothing.
- Cause: preview was a child of `SpindleBitTCP`, then `HideTcpDatum` set `Visible=false` on that node. `SceneNode.Draw` skips the whole subtree.
- Fix: `AttachPreview` parents the cylinder to the tool holder (same world pose). Plane stays hidden.
- Tests 14 passed.

### 2026-08-16 — SpindleBitTCP plane is the TCP / bit Z

- New `spindle.glb` has material `SpindleBitTCP` on a plane (`Mesh_0.001`). Green bit was 90° off (horizontal) vs the purple shop line (down).
- Cause: preview + TCP followed the housing long axis, not the authored plane normal.
- Fix: prefer `SpindleBitTCP`; origin on the plane; +Z = plane normal flipped away from the housing. Hide the plane (datum only). Legacy `SpindleBit` still uses housing axis.
- Files: `SpindleBitCylinder.cs`, `ViewportView.axaml.cs`, `SpindleBitCylinderTest.cs`. Tests 13 passed.

### 2026-08-16 — ERP email/password login (slicer ready; Lab route pending)

- Preferences → Connections: Email + Password. Connect / launch POSTs `/api/slicer/v1/login` and stores the returned bearer. Pasted API token still works.
- Password scrubbed from `.mass` files. Console: `erp email` / `erp password`.
- Lab does not have this route yet (404). Prompt: `docs/ERP-Login-API-Replit-Prompt.md`.

### 2026-08-16 — massiveslicer:// opens a .mass; Board copies the project folder

- Symptom: MassiveSYSTEM Windows icon copied `Z:\Research\LFAM\MassiveSLICER` (the checkout), not the project mass-files folder.
- Fix (Board): copy is now `"Z:\Projects\…\mass Files\"` from the save row (Projects only). New logo button launches `massiveslicer://open?path=`.
- Fix (Slicer): register `massiveslicer` URL protocol on Windows launch. `GET/POST /open` on the localhost bridge opens the .mass. A second instance hands off to the running app and exits.
- Files: `ProtocolUri.cs`, `ProtocolRegistration.cs`, `Program.cs`, `App.axaml.cs`, `LocalControlBridge.cs`, `macOS/Info.plist`, `ProtocolUriTest.cs`. Board `public/js/graph.js`.

### 2026-08-16 — Shared print + material presets via ERP (lab.massivemake.com)

- Print presets (`%AppData%/MassiveSlicer/presets.json`) and material presets (`materials.json`) were machine-local only. New MassiveSLICER installs could not see team libraries.
- **ERP contract (Replit):** `GET /api/slicer/v1/presets-bundle` plus CRUD on `/print-presets` and `/material-presets`. Same `Authorization: Bearer msl_…` as search/pricing. Payload is opaque desktop JSON (`PrintPresetRecord` / `MaterialPreset`) wrapped as `{ id, updatedAt, payload }`. Prompt: `docs/ERP-Presets-API-Replit-Prompt.md`.
- **Slicer (NAS clone `\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER`):** on ERP connect, pull bundle (404 → list endpoints → stay local). Merge by `ErpId` then name; POST local-only rows. Save preset / save material POST or PUT. Console: `erp presets`. 404 = “API not shipped yet” (connect still works).
- Files: `ErpClient.cs`, `ErpPresetSync.cs`, `ErpModels.cs`, `ErpViewModel.cs`, `PresetsCardViewModel.cs`, `MainWindowViewModel.cs`, `RightPanelView.axaml.cs`, `PrintPresetRecord.ErpId`, `MaterialPreset.ErpId`. Tests: `ErpParsingTest` 27 passed.
- **Not done:** Replit must still deploy the routes. Local `C:\Users\MassiveMAKE\MassiveSLICER` clone was not updated (NAS only).

### 2026-08-16 — LFAM 3 flange triad: Extruder tool roll spun X/Y

- Symptom: flange Z correct; green Y sat where red X should be.
- Cause: `FlangeFrameMatrix` used `totalRoll = toolFrameRoll + flangeDisplayRoll`. Extruder `toolFrameRoll=-90` rotated the physical flange mark.
- Fix: flange triad uses **only** `flangeDisplayRoll` (−15° on LFAM3). TCP/IK still use total roll. Rebuild launched.

### 2026-08-16 — Spindle cylinder was cocked off the housing

- Shop: green preview came out of the collet at an angle; purple line is the spindle (straight down). TCP should sit on that tip, just above the bed.
- Cause: cylinder +Z followed the SpindleBit puck thick axis (~31 mm X), which is not the housing spin axis.
- Fix: `FindHousing` = largest non-bit mesh; cylinder +Z = housing long axis, flipped away from the body. TCP still snaps to the cylinder tip.
- Files: `SpindleBitCylinder.cs`, `ViewportView.axaml.cs`, `SpindleBitCylinderTest.cs`.

### 2026-08-16 — MassiveSYSTEM Windows icon opens Explorer

- Symptom: graph Windows button only copied `Z:\…`.
- Cause: MassiveBOARD runs on the Mac, so `explorer.exe` never ran on the shop PC.
- Fix: `LocalControlBridge` `GET/POST /reveal?path=` starts Explorer on the folder (MassiveFILES / `Z:\Projects` / `Z:\Research` only). Graph calls `127.0.0.1:8723–8728` when the browser is Windows. Fallback still copies the path if slicer is not running.
- Files: `src/MassiveSlicer.App/Console/LocalControlBridge.cs`, MassiveBOARD `public/js/graph.js`.

### 2026-08-15 — KRL header records slicer version, preset, and slice settings

- After `DEF`: `;FOLD MassiveSLICER export` with status-bar `BuildInfo.Label`, UTC time, cell, TOOL/BASE.
- Print: material preset name (or none), type/color, HV/HF, layer height, bead width, extrusion flow, print/travel/wipe speeds, first-layer speed/RPM when set, extrusion RPM %, T1/T2/T3, approach Z.
- Mill: spindle RPM, cutting/plunge feed, rapid, approach Z.
- Injected into default / URM / mill / custom headers. ASCII only.

### 2026-08-15 — T12 TCP triad sat at the wrist; bit was on the table

- TOOL_DATA[12] applied in A6 put the RGB triad ~flange height. The cutter (green cylinder) was already on the bed. Pendant BASE 1 Z is about -99 mm (table).
- Spindle tools: snap TCP origin to the SpindleBit disc / preview-cylinder tip and align taught Z with the spindle. Extruder / scanner unchanged. TOOL_DATA numbers are not rewritten.

### 2026-08-15 — LFAM 3 flange triad rotated, mesh left alone

- Shop sketch: flange **X** along old Y (left), **Y** along -old X (down), **Z** unchanged (out of the face).
- Display-only `FlangeDisplayRotation` (+90 deg about Z) on `FlangeFrameMatrix`. TCP / TOOL_DATA / spindle GLB unchanged.

### 2026-08-15 — TOOL CONVENTION defaults to Z- (backward)

- Startup + null fallback is **Z- (backward)**, not Undefined. Combo, triad readout, and `ToolAxisConventionOption.Default` all agree.

### 2026-08-15 — TOOL #12 from ROBOT CELL crashed; MILL then T12 worked

- ROBOT CELL TOOL # mounted T12 **and selected** the toolhead (gizmo + Desync + flash Additive). MILL first used `SuppressNextToolViewportSelect` so the spindle came up without that overlay.
- TOOL # now uses the same no-select mount. TCP / TOOL_DATA still load. Overlay select is try/caught.

### 2026-08-15 — TOOL #12 select crashed the app

- Selecting T12 on LFAM 3 ran mount on the GL thread. A null TOOL CONVENTION SelectedItem (Avalonia ComboBox) or a missing T12 flange holder could NRE and take the process down.
- Convention ComboBox now binds SelectedIndex. Mount no longer unmounts every tool if T12 has no holder. Mount + spindle-cylinder wrap in try/catch and log to `%TEMP%/massiveslicer-crash.log`.

### 2026-08-15 — TOOL CONVENTION dropdown on the ROBOT card

- Next to TCP OFFSET: Undefined / Z- (backward) / Z+ (forward) / X- (backward) / X+ (forward).
- Remaps the TCP triad after taught ABC. Does not change TOOL_DATA or TCP ACTUAL numbers.

### 2026-08-15 — ROBOT card shows only TCP ACTUAL in the current BASE

- Removed FLANGE (ROBROOT), TCP WORLD, and BASE FRAME readouts from the left ROBOT card.
- One block: **TCP ACTUAL (BASE n)** XYZABC. Live sync = controller `$POS_ACT`. Otherwise scene TCP minus ROBROOT minus `bed.baseData`.

### 2026-08-15 — Name the two TCP triads + small x/y/z

- The two RGB sticks are **TCP** (T12 + mounted tool name) and **FLANGE**. Sensor (if present) is **SENSOR**.
- Small **x / y / z** at each tip: red / green / blue.

### 2026-08-15 — Spindle Show Cylinder was 1000x too big

- Tool GLBs bake verts to metres (`NormalizeMetresIfLooksLikeMillimetres`); flange applies GltfToScene ×1000. The preview cylinder was created in millimetres, so a Ø76 mm × 1 mm stick-out became 76 m × 1 m — larger than the cell.
- `MmToParentLocal` converts UI mm into the disc's local units (metres when AABB diag &lt; 10).

### 2026-08-15 — Save Home Position sits under Joint Angles

- Moved HOME POSITIONS (name + SAVE AS HOME POSITION) in the left ROBOT card to just below the A1–E1 sliders and above LIMITS.

### 2026-08-15 — Remove GO TO BED CENTER from left ROBOT card

- Dropped the `PrimaryButton` in `LeftPanelView.axaml`. `GoToBedCenterCommand` stays on `RobotPanelViewModel` (unused from UI).

### 2026-08-15 — Remove nested scroll in ROBOT / VIEWPORT

- VIEWPORT had `MaxHeight=520` ScrollViewer inside LeftPanelHost (wheel stole events, expand-to-top fought the inner offset).
- ROBOT had a Disabled ScrollViewer wrapper. Both unwrapped — the left column is the only scroller.

### 2026-08-15 — Left + right sidebar: expand pins the card to the top

- VIEWPORT only moved a few pixels because it is the last card — a scroller cannot pin the last item without empty space below. Inject a pad ~column height.
- ROBOT still failed when Offset was applied before Extent grew, or the inner Disabled viewer was targeted. Shared `SidebarExpandScroll` skips Disabled, waits for layout, binds Floating `Offset`.
- Same behavior on the **right** tab ScrollViewers (PRINTING / SCAN / MILL StepCards).

### 2026-08-15 — ROBOT expand scrolls the left column to that card

- Clicking ROBOT expanded in place because (1) the expand hook could miss the card and (2) scroll ran before layout, when Extent was still the collapsed height.
- Now a class handler on every StepCard, then LayoutUpdated + delayed retries, using the card's Y inside the scroll content.
- Floating ScrollViewer template also got a vertical scroll gesture so Offset actually moves the column.

### 2026-08-15 — MassiveBOARD lists last 10 .mass saves

- MassiveSLICER already logs each save (`WorkspaceSaveLog` → AppData + `Projects/_slicer/workspace-saves.jsonl`).
- Added `ReadRecent` for newest-first unique paths. Board `GET /api/slicer/saves` reads that JSONL (prefs `RecentWorkspaces` until the first log line).
- Expand MassiveSLICER on the graph: last 10 files. **Mac** reveals in Finder (`open -R`); **Windows** copies `Z:\…`.

### 2026-08-15 — Log every .mass save for MassiveLAB

- After a successful workspace save, append one JSONL line (path, UNAS-relative path, bytes, cell, project/element) to `%AppData%/MassiveSlicer/workspace-saves.jsonl` and `Projects/_slicer/workspace-saves.jsonl`.
- If Lab is connected, POST `/api/slicer/v1/workspace-saves` (404 is fine until Lab ships it). Does **not** create a slice rev.
- Share-relative paths now cover shop `Z:\Projects\…` and UNC `\\192.168.0.191\MassiveFILES\…`, not just `/Volumes/…`.

### 2026-08-15 — PRINT / SCAN / MILL swap the flange toolhead

- Clicking WORKFLOW PRINT / SCAN / MILL now mounts Extruder / Scanner (Calibrated) / Spindle (No Bit) on LFAM 3.
- Does **not** select the TCP in the viewport (no gizmo steal). Sidebar tabs still follow the phase.

### 2026-08-15 — Hide leftover seam-guide green line

- The thin lime line beside the LFAM 3 table was a **seam-guide column** (always-on-top, ~1 m stub when no part is loaded).
- Committed guides no longer draw unless a visible model or toolpath is in the scene. Open the seam editor to see/edit them.

### 2026-08-15 — Viewport top chrome: no overlap on small screens

- Move/rotate/scale bar (center) and Body/Toolpath/Speed/RPM/Thermal/Preview pills (right) overlapped when the viewport strip was narrow.
- If they would collide, the pills drop to a second row (centered). Wide windows stay one row.

### 2026-08-15 — Left sidebar: expand ROBOT (etc.) slides that card to the top

- First pass scrolled on a single Loaded post, before the expander body measured — Extent was still collapsed, so ROBOT stayed put.
- Now retries across layout/render (up to 10 frames) and targets `LeftPanelHost`, not the ROBOT card’s inner disabled ScrollViewer.

### 2026-08-15 — Imported KRL: drop it to the plate, and drag-drop it in

**Drop to Plate works on an imported toolpath.** Two separate blockers: the button was hidden
whenever a toolpath was selected (`IsVisible="{Binding !IsToolpathSelected}"`), and
`DropToPlate` bailed on `LayFlatMinZ` returning `MaxValue` — that walks mesh vertices, and a
KRL program has no mesh, only move endpoints. `NodeMinZWithToolpaths` now measures both.
Lay on Face stays mesh-only and stays hidden: it needs a face to rest on, a drop only needs a
lowest point. Travels count — one dipping below the extrusions still hits the bed.

**Trap, and it shipped a broken build before being caught: a registered toolpath keeps
ABSOLUTE points, but `SceneRenderer` sets the node transform to the toolpath CENTROID and both
renderer and exporter draw `(point − origin) × world`.** Transforming the raw point
double-counts the centroid. The first version did exactly that, so the drop overshot by the
centroid height and buried the part under the bed. Use `_toolpathOriginByNode` — the same
origin the exporter passes as `NodeOrigin`.

*Why the tests missed it:* all five used an identity transform and a zero origin, where the
bug is invisible. Added two that model how the scene really holds a toolpath. **A fixture that
cannot express the bug is not coverage.**

**Dropping a `.src` onto the app imports it.** Both targets were mesh-only: the viewport
filtered on `ImportHelper.IsSupported` and discarded it silently, while the left panel's drop
zone handed it to the mesh loader and logged an import failure it never deserved. Both now
route `.src`/`.krl` to `ImportKrlToolpath`, the same call the menu item makes.

**Related, landed on main separately (`374b534`):** KRL import now offsets by the drawn Print
Bed 0,0,0 (`Bed.BaseMarkerWorld`) rather than ROBROOT + BASE_DATA — the actual reason imported
programs floated metres in the air — plus E1 rail replay for programs carrying baked E1
(`KrlToolpathParser.HasProgrammedE1`; SRC E1 is authoritative and is never replanned).

**Safe to move a toolpath:** `KrlExporter` applies `NodeWorldTransform` to every point
(`KrlExporter.cs` ~1054), so a drop shifts exported coordinates, not just the display. Verified
before building — a version that moved only the render would have printed in the old place.

### 2026-08-15 — save.ps1: no stash on SMB

- Stash failed on shop PC: `unable to create file save.sh: File exists` while resetting the index (SMB).
- Flow is now **commit → pull → push** (no stash). Leftover `save.* auto-stash` is cleared.
- Every git call still uses `-c safe.directory=*`.

### 2026-08-15 — save.sh is SMB-safe (no more 700-file chmod dumps)

- `core.filemode=false`, `core.trustctime=false`. Stale `index.lock` dropped if no git process.
- After `git add -A`, chmod-only (`numstat 0/0`) is unstaged. `/install.sh` (Hermes installer) is gitignored.
- Tree is otherwise clean: only real KRL-import / sidebar work remains until the next save.

### 2026-08-15 — KRL import replays LFAM 1 E1 rail

- Parser now keeps inline `E1` on each LIN/PTP (holds last value when a later frame omits it).
- Imported E1 is authoritative: validation does **not** wipe or replan it. Rail moves on scrub/play even when Additive **E1 motion** is off.
- Import populates `_e1MmByNode` and kicks reachability so IK is rail-relative.

### 2026-08-15 — KRL import uses Print Bed 0,0,0

- Offset is the drawn **BASE / Print Bed 0,0,0 marker** (`Bed.BaseMarkerWorld` = ROBROOT XY + `baseData` XY, Z = `bed.origin.Z`).
- Do **not** parent the path under the bed node: toolpaths are drawn with `LocalTransform * mvp` (not world), so parenting put LFAM 1 imports on the rail at Z≈0, below the plate.
- LFAM 1 marker `(1475.51, -609.30, 70)`. LFAM 3 `(2135.45, -52.54, 916.31)`.

### 2026-08-15 — Left sidebar: expand a bottom card scrolls it to the top

- Expanding a collapsed left **StepCard** (ROBOT, VIEW, …) scrolls `LeftPanelHost` so that card’s header sits at the top of the column.
- Skips PersistExpander restore-on-load so startup doesn’t jump.

### 2026-08-15 — 5% software-limit envelope on sim / IK / export

- Shared `JointLimitEnvelope` (5% of each axis travel inside `$SOFTN_END`/`$SOFTP_END`).
- Viewport sliders, numerical IK, analytic IK `InLimits`, E1 rail planner, and toolpath validation all use the envelope.
- LIMITS expander still shows/edits the raw KRC stops. Export warns on moves outside the envelope.
- Wrist singularity still `|A5| < 5°`.

### 2026-08-14 — ROBOT LIMITS editor (LFAM 1 $machine.dat)

- Left **ROBOT** card: **LIMITS** expander — A1–A6 + E1 min/max (`$SOFTN_END` / `$SOFTP_END`).
- Seeded **LFAM 1** from live `\\192.168.0.151\krc\ROBOTER\KRC\R1\Mada\$machine.dat`.
- Edit writes to the connected KRC via C3Bridge. **Requires KUKA cold reboot** banner on change.

### 2026-08-14 — LFAM 3 Tool 12 re-taught on KRC

- Live `.153` `$ACT_TOOL=12`: `TOOL_DATA[12] = X -78.399, Y 325.229, Z 637.358, A 103.677, B -43.719, C 40.483`
- Synced three `lfam3.json` copies. LOAD_DATA[12] unset after pendant save.

### 2026-08-14 — Mill bit library: spindle cylinder locked to SpindleBit

- Tool library **Cutter** tab: **SPINDLE CYLINDER**. Origin = disc centroid; length along rotational-symmetry axis (not vertex-normal average).

### 2026-08-14 — Pattern scope: texture the skin, leave bracing straight (main 571)

**Shipped** (`PatternScope` in `SliceSettings`, picker at the top of PATTERN AND TEXTURE):
`Everything` / `WallsOnly` / `VisibleSkin`. Wave and Pattern displace only what is in scope.

- `ToolpathMove.IsWall`, set only by `ContourSeamPlanner` — the one place perimeters are
  emitted, so infill / X-bracing / Formbound / supports default to structure.
- `SkinOnlyBracing`: structure left out of scope stays straight, but an **open** run's ends
  take the displacement of the nearest wall point, blended linearly along the run. Linear
  blending is what keeps a brace straight — a straight segment under a linearly varying
  displacement is still straight. **Closed** runs (cavity boundaries) are left exactly where
  the slicer put them: they have no ends to keep attached, and translating a whole loop toward
  one side of a wavy skin only drags it off true.
- Fixed en route: both effects rebuilt moves keeping only `Normal`/`IsLayerChange`, silently
  dropping `IsBrim`, `HeightScale`, `PrintSpeedScale`, `IsWipe` — so brim RPM and adaptive
  layer-height flow were discarded on any patterned wall.

**A nesting-depth scope was built, shipped, and then REMOVED by taqotaqo** (`2474456`),
superseded by their raycast (`d7fd2bb`). Worth knowing why, because the reasoning generalises:
classifying a wall as outer-vs-interior by contour nesting asks "is this contour inside
another one", and **open chains have no answer**. Scanned and organic parts slice into open
chains — measured on one, all **6,676,002** wall moves came back at depth 0, so every interior
rib was classified as outer surface and got textured. `VisibleSkin` instead sweeps horizontal
rays from every compass direction per layer: first thing hit is skin, anything shadowed behind
it stays straight. No closed contours required.

**Do not reach for contour nesting on this codebase's real parts.** It is correct only for
clean solid models; the LFAM workload is largely scanned/organic.

**Still open — on `feature/pattern-skin-only`, NOT merged, may still reproduce on main:**
selecting a non-print-object (an effector, a modifier gizmo) breaks slicing two ways. Auto-slice
resolves to an item with no toolpath and takes the fresh-import path into the size guard; and
`SliceCommand` requires `HasMeshSelected`, which an effector selection clears — so the only
control that could rescue a scene above the auto-slice triangle limit disables itself exactly
when it is needed. Combined with the 1M-triangle guard this reads as "the setting did nothing"
while the toolpath is simply stale. Verify against main before porting the fix.

**Also on that branch and now dead:** the `walls` console command and the single-skin depth
realignment both depend on the removed nesting mechanism.

### 2026-08-07 — Seam guide traces the model surface (merged; five failed attempts first)

**Shipped:** `BuildSeamGuideSurfaceProfile` in `ViewportView.axaml.cs`. Cuts the model with a
vertical plane through the part axis and the guide, keeps the intersection points on the
guide's side, and takes the outer edge per height band. Exact plane/triangle intersection —
one pass over triangles, strided to 60k on big meshes. Confirmed working end to end: Edit →
guide follows the surface → click → Save → re-slices with the seam at the guide.

**Viewport only. The slicer was NOT changed** (verified: `git diff origin/main...branch` was a
single file). An experiment that made the guide anchor every layer instead of only the birth
layer was reverted — see below.

**Two bugs found after the profile worked:**
- *Worked once, never again.* The profile only collected **visible** models. Slicing hides the
  model and shows the toolpath, so re-opening the editor on a sliced part found no mesh and
  fell back to a straight column. Now prefers shown models, falls back to hidden.
- *One guide only.* `AddSeamGuidePoint` clears before adding. The slicer resolves one guide per
  closed contour, so extra points on a single-island part were dead weight that stacked in the
  list and blocked each other from deletion. Placing again moves the seam.

**Four approaches that FAILED — do not retry these:**
1. Nearest printed extrude point per layer → snapped to solid-cap/infill beside the axis; drew
   a line straight up the middle. `ToolpathMove` has **no wall-vs-infill flag**.
2. Outermost printed point in the guide's compass direction → jagged, because the ±0.02 cosine
   tolerance spans a wide arc on a big part and flips between corners.
3. Mesh **vertices** banded by height → sparse scatter per band, selection flipped between
   distant vertices, produced a zigzag scribble.
4. First-contour-only nearest point → sound idea, but tangled with a stale hidden toolpath and
   produced a curve floating clear of the part.

**The lesson:** 1–4 all *sampled* points and scored them with an invented heuristic. Sampling
plus a heuristic is what produced every artefact. The fix was exact geometry (plane section),
where there is no scoring rule to get wrong.

**Reverted — the every-layer seam experiment.** Making a guide re-solve on every layer rather
than only the birth layer *seemed* to explain guide/seam drift on a flared column. Tests
written for it (`SeamGuideEveryLayerTest`, now deleted) **passed against the unmodified slicer
too** — neither a flaring tube nor a 90° twisting tube separates the two rules, because
nearest-point inheritance holds a near-constant XY on both. Shipped anyway, did not fix the
symptom, reverted. **If guide/seam drift is reported again, get the `.mass` file and measure
the per-layer seam positions — do not reason from screenshots.** Five attempts in this session
were screenshot-driven and every one was wrong.

**Also fixed earlier in the same run:** guide markers rebuild in `StageToolpathMaps` after a
slice (the curve is traced from geometry that a re-slice replaces).

### 2026-08-06 — Seam guides follow the wall (and why guide ≠ seam on a flare)

**Change:** a guide drew as a straight vertical column at one XY. It now draws as a polyline —
for each printed layer, the point on that layer's **outer boundary lying in the guide's compass
direction from the layer centre**, swept as a tube bottom to top (`BuildSeamGuidePath` in
`ViewportView.axaml.cs`, `AppendTube` in `SeamGuideColumnRenderer`).
`SetSeamGuides`/`SetSeamGuidePreview` take paths;
`PickSeamGuide` hit-tests every projected segment. Falls back to the straight column when
nothing is sliced; the separate toolpath seam-drag tool gets a short vertex tick
(`SeamVertexTick`) because it marks one loop, not a wall.

**Why it mattered beyond looks:** on a flaring part the straight line stands well off the
surface, so guide and seam could not be visually compared at all — every report of "the seam
isn't where the guide is" was unmeasurable. The curve traces the same column of wall the
slicer picks, so the two are finally comparable.

**Trap — "nearest extrusion point" does not find the wall.** The first attempt took the
extrusion point nearest the guide XY on each layer. `ToolpathMove` has **no wall-vs-infill
flag** (`MoveKind` is only Extrude/Travel/Mill), and a solid cap or dense infill covers the
whole cross-section, so the nearest point was fill right beside the axis — the guide still
drew straight up the middle of the part. Direction-from-centre fixes it because the extreme
point along a direction is on the convex hull by construction, so fill can never win.

**Still expect divergence up high, by design:** the guide is consulted only on a *birth* layer
(`PlanarSlicer.cs` ~1211); every layer above projects the parent's seam onto its own contour
for continuity. On a strongly changing cross-section the seam can drift as it rises. Not
changed — it affects print output and is the developer's call whether every layer should
re-align to the guide.

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
