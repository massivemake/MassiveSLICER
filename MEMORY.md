# MassiveSlicer — Project Notes

## Project Overview

MassiveSlicer is a C# desktop application for KUKA robot CAM — generates toolpaths (additive/subtractive), exports KRL programs, previews robot motion, and connects live to the KRC4 controller via C3Bridge. Originally an Electron/JavaScript prototype; this is the C# rewrite.

The 3D stack uses Z-up right-hand coordinates throughout (KUKA convention). OpenTK/OpenGL is used instead of Three.js specifically because Three.js is Y-up native.

## Stack

| Layer | Technology |
|-------|-----------|
| UI framework | **Avalonia** (.NET, AXAML, MVVM) — cross-platform |
| 3D viewport | OpenTK (OpenGL) via `OpenGlControlBase` |
| Coordinate system | **Z-up, right-hand** throughout |
| Robot comms | C3Bridge (TCP to KRC4 at port 7000) — custom async client |
| IK/FK | Custom solver — D-H parameters, Z-up convention |
| Solution structure | Multi-project `.sln` |

## Solution Structure

```
MassiveSlicer.slnx
src/
├── MassiveSlicer.App/          # Avalonia application (entry point, UI shell)
│   ├── App.axaml
│   ├── MainWindow.axaml
│   ├── Views/                  # AXAML views
│   ├── ViewModels/             # MVVM view models
│   └── Resources/              # Themes, styles
├── MassiveSlicer.Core/         # Pure C# business logic, no UI dependencies
│   ├── Models/                 # RobotConfig, Toolpath, SliceResult, etc.
│   ├── Slicing/                # Planar, angled slicers
│   ├── Kinematics/             # FK/IK solvers, D-H parameters
│   ├── IO/                     # STL/GLB/KRL loaders, KRL exporter, RCC extractor
│   └── C3Bridge/               # KRC4 TCP connection, joint streaming
├── MassiveSlicer.Viewport/     # OpenGL scene management (depends on OpenTK)
│   ├── Rendering/              # Shader programs, draw calls
│   ├── Scene/                  # SceneGraph, Mesh, Material
│   ├── Camera/                 # Orbit camera, Z-up projection
│   ├── FK/                     # Robot FK controller, GLTF IK solver
│   └── Loading/                # GLTF and STL loaders
└── MassiveSlicer.Tests/        # xUnit tests
```

**Dependency rule:** `Core` has no UI dependencies. `Viewport` depends only on `Core`. `App` depends on both.

## Commands

```bash
# Build entire solution
dotnet build

# Run the application (macOS / Linux / Windows)
dotnet run --project src/MassiveSlicer.App

# Run all tests
dotnet test

# Run a single test class
dotnet test --filter "FullyQualifiedName~KinematicsTests"

# Format code
dotnet format

# Publish self-contained macOS executable
dotnet publish src/MassiveSlicer.App -r osx-arm64 --self-contained -c Release

# Publish self-contained Windows executable
dotnet publish src/MassiveSlicer.App -r win-x64 --self-contained -c Release
```

## macOS Porting Changes

The project originally ran Windows-only. These changes were made to get it running on macOS (arm64, .NET 10):

### 1. Target framework: net8.0 → net10.0
All four `.csproj` files updated — macOS had .NET 10 installed, not .NET 8.

Files changed: `MassiveSlicer.App.csproj`, `MassiveSlicer.Core.csproj`, `MassiveSlicer.Viewport.csproj`, `MassiveSlicer.Tests.csproj`

### 2. GlHostControl — complete rewrite (`src/MassiveSlicer.App/Views/GlHostControl.cs`)

**Old approach (Windows-only):** Created a hidden 1×1 Win32 HWND, used WGL P/Invoke (`kernel32.dll`, `user32.dll`, `gdi32.dll`, `opengl32.dll`) to create an OpenGL context, rendered to an offscreen FBO, then read pixels back to an Avalonia `WriteableBitmap`.

**New approach (cross-platform):** Inherits from Avalonia's `OpenGlControlBase`, which handles context creation via CGL on macOS / WGL on Windows / EGL on Linux. Scene renders into a depth-backed intermediate FBO (so `SceneRenderer` has depth buffer support), then blits colour to the framebuffer Avalonia provides. No pixel readback needed — Avalonia composites the GL result directly.

Key change: `OpenTK.IBindingsContext` is implemented with an adapter wrapping `Avalonia.OpenGL.GlInterface.GetProcAddress` so OpenTK's `GL.LoadBindings()` resolves entry points from the Avalonia-managed context.

### 3. Program.cs — macOS OpenGL platform options

Added `AvaloniaNativePlatformOptions` for macOS to ensure the Avalonia native platform exposes an OpenGL context (needed for `OpenGlControlBase`):

```csharp
if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    builder = builder.With(new AvaloniaNativePlatformOptions
    {
        RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software]
    });
```

### 4. MassiveSlicer.App.csproj — removed `BuiltInComInteropSupport`

`<BuiltInComInteropSupport>true</BuiltInComInteropSupport>` is Windows COM interop; removed since it's not applicable on macOS.

## UI Layout

| Region | Description |
|--------|-------------|
| **Top toolbar** (48px) | File menu, Prepare/Preview mode toggle, view mode buttons, camera presets, unit selector, robot sync/status |
| **Left sidebar** (220px, resizable) | Robot cell selector + OUTLINER/ASSETS tabs + scene tree |
| **Center viewport** | OpenGL canvas, selection toolbar, transform gizmo, coordinate readout |
| **Right sidebar** (300–400px, dynamic) | ADDITIVE / SUBTRACTIVE / SETTINGS tabs |
| **Status bar** (24px) | File status, operation feedback |
| **Console** (floating) | Command history + input |

Right panel SETTINGS sub-tabs: VIEW, UV, ROBOT (joint sliders A1–A6, TCP readout, tool library), PROPS.

## MVVM Structure

```
MainWindowViewModel
├── ViewportViewModel
├── LeftPanelViewModel (OutlinerViewModel)
├── RightPanelViewModel
│   ├── AdditiveSettingsViewModel
│   ├── SubtractiveSettingsViewModel
│   └── SettingsViewModel
│       ├── ViewSettingsViewModel
│       ├── RobotPanelViewModel
│       └── PropsViewModel
└── StatusBarViewModel
```

## Coordinate System

All geometry, robot positions, and toolpath coordinates: **Z-up right-hand**
- X = forward, Y = left, Z = up

The OpenGL camera uses Z as the up vector from the start. Never apply a global Y-to-Z rotation — that was the Three.js problem this rewrite solves.

KUKA ABC angles (Euler ZYX convention) map directly; no axis remapping needed.

## Key Domain Concepts

- **KRL** — KUKA Robot Language; `.src` files with PTP/LIN/CIRC move sequences
- **C3Bridge** — Proprietary TCP protocol for live joint streaming from KRC4 (port 7000)
- **TCP** — Tool Center Point, the active tool tip offset (not TCP/IP)
- **BASE / TOOL_DATA** — KUKA frame indices (BASE 1–32, TOOL 1–16)
- **D-H parameters** — Denavit-Hartenberg convention for the KR120 R3900 arm
- **Slicing modes** — Planar (horizontal layers), Angled (tilted planes), Geodesic (surface-following)
