# MassiveSLICER V2 — Project Memory

Last updated: 2026-06-25 (milestone — KRL import + viewport polish)

> **Single source of truth** for humans and all AI assistants working in this repo. Session progress, architecture, conventions, and commands live here — **not** in tool-specific files (`CLAUDE.md`, etc.).

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
| Repo | `\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER V2\` |
| GitHub | https://github.com/MattWhite3194/MassiveSlicer |
| Publish (only) | `%LOCALAPPDATA%\MassiveSlicer\build` |
| Cell JSON (canonical) | Repo `assets\cells\` — dev saves mirror here via `CellPaths` |
| Test GLB | `assets\test\crystal_stone_rock.glb` |

**Do not use `build2`, `build3`, or `build4`** — obsolete copies from earlier sessions.

### Build + run (canonical — always paste this in full)

```powershell
Stop-Process -Name "MassiveSlicer.App" -Force -ErrorAction SilentlyContinue
Set-Location '\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER V2'
dotnet publish 'src/MassiveSlicer.App/MassiveSlicer.App.csproj' -c Release -o "$env:LOCALAPPDATA\MassiveSlicer\build"
if ($LASTEXITCODE -eq 0) {
    Start-Process -FilePath "$env:LOCALAPPDATA\MassiveSlicer\build\MassiveSlicer.App.exe" -WorkingDirectory "$env:LOCALAPPDATA\MassiveSlicer\build"
}
```

Equivalent script (same steps): `scripts\publish-and-run.ps1`

### Start only (no rebuild)

```powershell
Start-Process -FilePath "$env:LOCALAPPDATA\MassiveSlicer\build\MassiveSlicer.App.exe" -WorkingDirectory "$env:LOCALAPPDATA\MassiveSlicer\build"
```

---

## Completed features

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

1. **PBR polish (core done — see Completed features):** real metallic-roughness PBR with textures now renders. Remaining nice-to-haves: full prefiltered-env + BRDF LUT IBL (v1 uses roughness→LOD + analytic Karis fit); alpha **blend** ordering (v1 = Opaque + Mask only); populate `UvSettingsViewModel` from the selected mesh; later: apply the material system to toolpath meshes + feed the slicer.
   - **"Make it pop" — DONE via user sliders + default backdrop (2026-06-21):** a *hardcoded* in-shader exposure/IBL boost **grayed** the crystal (it's mostly metal → colour comes from albedo-tinted env reflection → brightening hits ACES's desaturation shoulder). So instead: added user-facing **Exposure** + **Reflections (IBL gain)** sliders in the LIGHTING panel (`ViewportViewModel.Exposure`/`IblIntensity` → `SceneRenderer` → per-mesh `MeshRenderer.Exposure`/`IblGain` → `uExposure`/`uIblGain`, defaults 1.0 = neutral) so the user dials it live. Also set a **non-None default backdrop** (`ViewportViewModel` ctor picks AmbienceExposure4k/CasualDay4K/… from `assets/Images/*.hdr`, fallback first image) so imported models get IBL out of the box. Verified: bumping the sliders brightens + glosses the crystal with colour intact.
   - **Import display:** the committed Final Render is colourful and correct (verified after a clean rebuild). If an imported model looks grey/flat, suspect a **stale NAS build** first (clean-rebuild Viewport), then check that `ApplyShaderModeToSubtree` ran (toggling a shader mode forces it).
2. **Optional cleanup** — delete obsolete `%LOCALAPPDATA%\MassiveSlicer\build2|build3|build4` folders.
3. ~~**KRL import**~~ — **done** (2026-06-25 milestone). Remaining: path-tangent IK for scrub on mill moves with zero normals; optional bead width from tool diameter.
4. **User verification** — confirm: no N tab on boot; N key opens HUD; LFAM3 timeline expands on click; **rivets aligned on connector** (done); **Pick/Deposit pills above active phase** after expand + phase select, details expand **upward** on pill click (session-6 Canvas fix); transform bar; rock select → Focus bar; Live I/O **Position** column shows A1–A6/E1 + TCP when robot synced; P1–P3 I/O live on LFAM 3 (`extIp` 192.168.0.196, `millIp` 192.168.0.249).
5. **Spindle RPM display** — not implemented; would need KUKA spindle `$ANOUT` or ATV340 Modbus (see LFAM 3 Live I/O map).

---

## Session changelog (reverse chronological)

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