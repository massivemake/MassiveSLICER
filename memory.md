# MassiveSLICER V2 — Project Memory

Last updated: 2026-06-21

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
- Refactored all 7 phase columns in `Lfam3WorkflowTimelineView.axaml`: `HeaderPad` → `Fold` → `PhaseDetails` / param cards.
- Larger phase icons in `BaseStyles.axaml` (76×76 host, 54px node, 32px icons).
- Cleaner phase borders and column structure; chevron/stem/connected layout removed.
- Live I/O toggle above workflow phases; expands into `Lfam3LiveIoPanelView`.
- **Live I/O Phase 2 (Pellet Extruder):** `ExtruderBridgeClient` polls `extIp:8765` every 2 s — flat `io` (Pos30 DI/DO, Pos28 `O_*`, MIO/RTD analog) + `modbus` holding regs (`hr_301xx`/`hr_302xx` zone temps). Writable DO with confirm on bridge pins. Status: `P2 live · bridge + Modbus`.
- **Live I/O Phase 3 (Milling Spindle):** `MillingModbusClient` polls `millIp:8765` every 3 s — `MILLING_IO` RevPi DIO names in bridge `io` dict. LFAM 3: `millIp` **192.168.0.249** (RevPi130866), `hasMilling: true`. Bridge deployed (`lfam-monitor.service` active). Status: `P3 live · bridge`.
- **Live I/O milestone (Phases 1–3):** `LiveIoPhasePlan` all **Implemented**; catalog in `Lfam3LiveIoCatalog.cs`; panel in `Lfam3LiveIoPanelView`; poll loops in `LiveIoMonitorViewModel`. See **LFAM 3 Live I/O map** below.
- LFAM 3 sidebar tab gating: Print → Additive, Scan → Scan, Mill → Subtractive (`SyncLfam3WorkflowSidebar`).

### LFAM 3 Live I/O map (Phases 1–3 complete)

**Cell endpoints** (`lfam3.json`):

| Subsystem | IP | Port | Protocol |
|-----------|-----|------|----------|
| KUKA C3Bridge | `bridgeIp` (cell) | 7000 | TCP JSON vars |
| Pellet extruder RevPi | `extIp` **192.168.0.196** | 8765 | lfam-monitor JSON bridge |
| Milling cabinet RevPi | `millIp` **192.168.0.249** | 8765 | lfam-monitor JSON bridge |

**Phase 1 — Robot (KUKA)** — `LiveIoSource.Kuka`, ~2×/s via C3Bridge when synced:
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

---

## Key files (quick reference)

| Area | Paths |
|------|-------|
| Cell load | `MainWindow.axaml.cs` (`SwitchCell`), `CellSceneLoader.cs`, `CellEnvironmentBuilder.cs`, `ViewportView.axaml.cs` (`ApplyCellSwap`) |
| Scene cache | `CellSceneCache.cs` (`Invalidate` for `reload-cell`) |
| Console | `ConsoleViewModel.cs`, `ConsoleCommandRegistry.cs`, `ConsoleView.axaml` |
| Workflow | `Lfam3WorkflowTimelineView.axaml`, `BaseStyles.axaml`, `ViewportViewModel.cs` |
| Live I/O | `LiveIoMonitorViewModel.cs`, `ExtruderBridgeClient.cs`, `MillingModbusClient.cs`, `Lfam3LiveIoCatalog.cs`, `LiveIoPhasePlan.cs` |
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
4. **User verification** — confirm: no N tab on boot; N key opens HUD; LFAM3 timeline; transform bar; rock select → Focus bar; Live I/O panel shows P1–P3 live on LFAM 3 (`extIp` 192.168.0.196, `millIp` 192.168.0.249).
5. **Spindle RPM display** — not implemented; would need KUKA spindle `$ANOUT` or ATV340 Modbus (see LFAM 3 Live I/O map).

---

## Session changelog (reverse chronological)

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