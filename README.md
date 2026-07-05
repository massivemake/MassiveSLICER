# MassiveSlicer

A C# desktop CAM application for KUKA robot additive and subtractive manufacturing. Generates toolpaths, exports KRL programs, previews robot motion in 3D, and connects live to a KUKA KRC4 controller via C3Bridge.

## Requirements

| Requirement | Version |
|-------------|---------|
| .NET SDK | 8.0 or newer |
| OS | Windows 10/11 (primary) or macOS 12+ |
| GPU | OpenGL 4.1+ |

---

## Windows

### Install .NET 8 SDK

Download from [https://dotnet.microsoft.com/download/dotnet/8.0](https://dotnet.microsoft.com/download/dotnet/8.0) and run the installer. Verify:

```powershell
dotnet --version
# Should print 8.x.xxx
```

### Clone the repository

**Canonical repo:** [github.com/massivemake/MassiveSLICER](https://github.com/massivemake/MassiveSLICER) — always work on the `main` branch.

```powershell
git clone https://github.com/massivemake/MassiveSLICER.git
cd MassiveSLICER
```

> **Note:** Older checkouts under `MassiveSlicer` (MattWhite3194) or `Slicing/MassiveSLICER` are deprecated. Use this repo only.

### Build

```powershell
dotnet build
```

### Run

```powershell
dotnet run --project src/MassiveSlicer.App
```

### Publish a standalone executable (optional)

```powershell
dotnet publish src/MassiveSlicer.App -r win-x64 --self-contained -c Release
# Output: src/MassiveSlicer.App/bin/Release/net8.0/win-x64/publish/MassiveSlicer.App.exe
```

---

## macOS

### Install prerequisites

**.NET SDK 9.0 or newer** — the solution uses the `.slnx` format, which the .NET 8 SDK
cannot parse. Download from [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download)
and choose the macOS arm64 installer for Apple Silicon (M1/M2/M3) or x64 for Intel. Verify:

```bash
dotnet --version
# Should print 9.x.xxx or newer
```

> **Note:** On Apple Silicon, install the **arm64** SDK (not Rosetta). The app builds and
> runs natively as `net8.0` / arm64 on macOS — no x64 emulation needed.

**Git LFS** — the repo stores binary assets (GLB/STL/3dm/HDR) via Git LFS:

```bash
brew install git-lfs
git lfs install
```

### Clone the repository

```bash
git clone https://github.com/massivemake/MassiveSLICER.git
cd MassiveSLICER
git lfs pull   # ensure binary assets are materialized
```

### Build

```bash
dotnet build MassiveSlicer.slnx
```

### Run

```bash
dotnet run --project src/MassiveSlicer.App
```

### Publish a standalone app bundle (optional)

```bash
dotnet publish src/MassiveSlicer.App -r osx-arm64 --self-contained -c Release
# Intel Mac: use osx-x64 instead of osx-arm64
```

> **macOS limitation:** STEP (`.stp` / `.step`) import is **Windows-only** — it depends on
> Open CASCADE (Occt.NET), which ships Windows-only native libraries. All other import
> formats (STL, OBJ, 3MF, GLTF/GLB) and the rest of the app work on macOS. Attempting a
> STEP import on macOS is handled gracefully (the file is skipped, no crash).

---

## Project Structure

```
MassiveSlicer.sln
src/
├── MassiveSlicer.App/          # Avalonia UI application (entry point)
├── MassiveSlicer.Core/         # Business logic — slicing, kinematics, KRL export
├── MassiveSlicer.Viewport/     # OpenGL 3D viewport (OpenTK)
└── MassiveSlicer.Tests/        # xUnit unit tests
assets/
├── cells/                      # Robot cell configurations (JSON + GLB models)
│   ├── LFAM2/                  # LFAM2 cell — HV Extruder + HF Extruder
│   └── LFAM3/                  # LFAM3 cell — HV Extruder + Zivid 3D scanner
tools/
└── ZividScanTest/              # Standalone Zivid SDK test utility
```

---

## Running Tests

```powershell
dotnet test
# Run a specific test class:
dotnet test --filter "FullyQualifiedName~KinematicsTests"
```

---

## Hardware

| Device | Details |
|--------|---------|
| Robot | KUKA KR120 R3900 on KRC4 controller |
| Live connection | C3Bridge TCP/WebSocket at port 7000 on the KRC4 IP |
| 3D Scanner (LFAM3) | Zivid 2+ MR60 at `192.168.0.150` (Zivid SDK 2.16) |

---

## Coordinate System

All geometry, toolpaths, and robot positions use **Z-up right-hand** coordinates (X = forward, Y = left, Z = up) throughout — matching KUKA KRL conventions directly with no axis remapping.
