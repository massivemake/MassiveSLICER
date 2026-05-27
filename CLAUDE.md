# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

MassiveSlicer is a C# desktop rewrite of a KUKA robot CAM application (originally an Electron/JavaScript prototype at `C:/Users/matth/OneDrive/Desktop/repos/MassiveSlice`). It generates toolpaths (additive/subtractive) for KUKA KRC4 robots, exports KRL programs, previews robot motion, and connects live to the robot controller via C3Bridge.

The original prototype's UI layout, panel structure, and workflows are the reference for the rewrite. The 3D stack is being replaced specifically because Three.js is Y-up native while KUKA robots and all CAM conventions in this project use **Z-up right-hand** coordinates.

## Stack

| Layer | Technology |
|-------|-----------|
| UI framework | WPF (.NET 8), XAML, MVVM |
| 3D viewport | OpenTK (OpenGL) embedded in WPF via `OpenTK.WPF` |
| Coordinate system | **Z-up, right-hand** throughout — enforced at the rendering layer |
| Robot comms | C3Bridge (TCP/WebSocket to KRC4 at port 7000) — built from scratch |
| IK/FK | Custom solver — built from scratch, not ported from JS |
| Solution structure | Multi-project `.sln` |

## Solution Structure

```
MassiveSlicer.sln
src/
├── MassiveSlicer/              # WPF application (entry point, UI shell)
│   ├── App.xaml
│   ├── MainWindow.xaml
│   ├── Views/                  # XAML pages/windows
│   ├── ViewModels/             # MVVM view models (INotifyPropertyChanged)
│   ├── Controls/               # Reusable custom controls
│   └── Resources/              # Themes, styles, dictionaries
├── MassiveSlicer.Core/         # Pure C# business logic, no UI dependencies
│   ├── Models/                 # Data types: RobotConfig, Toolpath, SliceResult, etc.
│   ├── Slicing/                # Planar, angled, geodesic slicers
│   ├── Kinematics/             # FK/IK solvers, D-H parameters, joint limits
│   ├── Robot/                  # KUKA KR120 model, tool library, start positions
│   ├── IO/                     # STL/GLB/KRL loaders, KRL exporter
│   └── C3Bridge/               # KRC4 TCP connection, joint streaming
├── MassiveSlicer.Viewport/     # OpenGL scene management (depends on OpenTK)
│   ├── Renderer/               # Shader programs, draw calls
│   ├── Scene/                  # SceneGraph, Mesh, Material, lights
│   └── Camera/                 # Orbit camera, projection (Z-up)
└── MassiveSlicer.Tests/        # xUnit tests for Core and Kinematics
```

**Dependency rule:** `Core` has no dependencies on `Viewport` or `MassiveSlicer` (WPF). `Viewport` depends only on `Core`. The WPF project depends on both.

## Commands

```powershell
# Build entire solution
dotnet build

# Run the application
dotnet run --project src/MassiveSlicer

# Run all tests
dotnet test

# Run a single test class
dotnet test --filter "FullyQualifiedName~KinematicsTests"

# Format code
dotnet format

# Publish self-contained Windows executable
dotnet publish src/MassiveSlicer -r win-x64 --self-contained -c Release
```

## UI Layout Reference

The original app (`MassiveSlice/src/renderer/index.html`) defines the panel structure to replicate:

| Region | Description |
|--------|-------------|
| **Top toolbar** (48px) | File menu, Prepare/Preview mode toggle, view mode buttons, camera presets, unit selector, robot sync/status |
| **Left sidebar** (220px, resizable) | Robot cell selector + OUTLINER/ASSETS tabs + scene tree |
| **Center viewport** | OpenGL canvas, selection toolbar, transform gizmo, coordinate readout |
| **Right sidebar** (300–400px, dynamic) | ADDITIVE / SUBTRACTIVE / SETTINGS tabs |
| **Status bar** (24px) | File status, operation feedback |
| **Console** (floating) | Command history + input |

**Right panel SETTINGS sub-tabs:** VIEW (themes, lights, environment, shader modes), UV (UV viewer + texture slots), ROBOT (joint sliders A1–A6, TCP readout, start positions, tool library), PROPS (geometry stats, bed/robot cell properties).

## MVVM Pattern

Each panel has a corresponding `ViewModel`. ViewModels expose observable properties and `ICommand` implementations. Views bind to ViewModels only — no code-behind logic beyond XAML event wiring.

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

## Coordinate System Convention

All geometry, robot positions, and toolpath coordinates are stored and computed in **Z-up right-hand**:
- X = forward
- Y = left
- Z = up

The OpenGL camera is configured with a Z-up projection from the start (view matrix uses Z as the up vector). Never apply a global Y-to-Z rotation hack — that was the Three.js problem this rewrite is solving.

KUKA ABC angles (Euler ZYX convention) map directly; no axis remapping needed.

## Key Domain Concepts

- **KRL** — KUKA Robot Language; `.src` files define robot move sequences (PTP/LIN/CIRC commands with frame data)
- **C3Bridge** — Proprietary TCP protocol for live joint streaming from KRC4 controller (port 7000)
- **TCP** — Tool Center Point, the active tool tip offset (not the networking protocol)
- **BASE / TOOL_DATA** — KUKA frame indices (BASE 1–32, TOOL 1–16) that define workpiece and tool coordinate frames
- **D-H parameters** — Denavit-Hartenberg convention used for FK/IK of the KR120 R3900 arm
- **Slicing modes** — Planar (horizontal layers), Angled (tilted planes), Geodesic (surface-following conformal paths)

## What Is NOT Ported from the Prototype

- Three.js rendering code — replaced entirely with OpenTK/OpenGL
- IK solver (`kinematics.js`) — rewritten in C# with proper Z-up convention
- C3Bridge (`c3bridge.js`) — rewritten from scratch in C# as async TCP client
- Global state in `main.js` — replaced by MVVM ViewModels and `Core` models
