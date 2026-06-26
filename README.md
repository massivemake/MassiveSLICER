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

### Install .NET 8 SDK

Download from [https://dotnet.microsoft.com/download/dotnet/8.0](https://dotnet.microsoft.com/download/dotnet/8.0). Choose the macOS arm64 installer for Apple Silicon (M1/M2/M3) or x64 for Intel. Verify:

```bash
dotnet --version
# Should print 8.x.xxx
```

> **Note:** On Apple Silicon, the default .NET installer targets `arm64`. If you hit architecture issues, ensure you installed the arm64 SDK (not Rosetta).

### Clone the repository

```bash
git clone https://github.com/MattWhite3194/MassiveSlicer.git
cd MassiveSlicer
```

### Build

```bash
dotnet build
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
