# MassiveSLICER V2 — Project Memory

Last updated: 2026-06-21 (session 6)

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

### Console commands
- `ConsoleCommandRegistry.cs`, `ConsoleViewModel.cs`, `ConsoleView.axaml`.
- Typed commands with Tab/↑↓ autocomplete and Enter to run.
- Commands: `help`, `clear`, `new`, `open`, `save`, `save-as`, `settings`, `panel-settings`, `import`, `import-krl`, `undo`, `redo`, `console`, `right-panel`, `frame`, `prepare`, `preview`, **`reload-cell`**.
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
| Milling bridge deploy | `scripts/deploy_bridge_lfam3_milling.py` (GitHub: [scripts/deploy_bridge_lfam3_milling.py](https://github.com/MattWhite3194/MassiveSlicer/blob/main/scripts/deploy_bridge_lfam3_milling.py)) |
| GL host | `GlHostControl.Windows.cs`, `ViewportView.axaml` |
| Overlay | `ViewportOverlayView.axaml` |
| Import | `ImportHelper.cs`, `GltfImportInspector.cs`, `GltfLoader.cs`, `AssetLocalCache.cs` |
| Tests | `CellSceneLoadTest.cs`, `GltfImportTest.cs`, `Lfam3LoadTest.cs` |

---

## Pending / not started

1. **PBR / textured GLB rendering** — UVs, normal maps, albedo textures in `MeshData` / `GltfLoader` / `MeshRenderer`; `UvSettingsViewModel` still stub.
2. **Optional cleanup** — delete obsolete `%LOCALAPPDATA%\MassiveSlicer\build2|build3|build4` folders.
3. **KRL import** — parser not implemented (console/file menu stub only).
4. **User verification** — confirm: no N tab on boot; N key opens HUD; LFAM3 timeline expands on click; **rivets aligned on connector** (done); **Pick/Deposit pills above active phase** after expand + phase select, details expand **upward** on pill click (session-6 Canvas fix); transform bar; rock select → Focus bar; Live I/O **Position** column shows A1–A6/E1 + TCP when robot synced; P1–P3 I/O live on LFAM 3 (`extIp` 192.168.0.196, `millIp` 192.168.0.249).
5. **Spindle RPM display** — not implemented; would need KUKA spindle `$ANOUT` or ATV340 Modbus (see LFAM 3 Live I/O map).

---

## Session changelog (reverse chronological)

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