# Improved Cell Frames — Adaptation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.
> Branch: `feature/Improved-Cell` (already exists, tracks `origin/feature/Improved-Cell` at `0baf2e0`). Do **not** commit to `main`. This changes robot motion / IK / cell frames — keep it off `main` until LFAM 3 mill scrub + a print export are verified on the machine.

**Goal:** Steal the *kinematics contract* from the Robotics Library / RAPID proposal (named frames, cell-owned chain, TCP is not a joint, mill/print query the cell) and implement it in C# on the existing GLB FK — without importing `rl::mdl`, entity UUIDs, or `wobj0`/`tool0`.

**Architecture:** Add a small Core model of **named frames** and a **tool kinematics spec**. Viewport IK stays `GltfNumericalIkSolver` (DLS on a fixed flange-local TCP). Mill and print stop inventing their own TCP meaning; they pass a target pose in BASE and a `SolveRecipe`. External axes stay typed: rail = moving ROBROOT, rotary = work-object spin.

**Tech Stack:** C# / net8, existing `SceneNode` graph, `RobotFkController`, `GltfNumericalIkSolver`, cell JSON (`assets/cells/LFAM*/`). No new native deps.

---

## Do not adapt (non-goals — reject if a task drifts here)

| Proposal item | Why we skip it |
|---|---|
| `rl::mdl::Model` / `JacobianInverseKinematics` | Viewport IK exists so joints match `LFAM3Robot.glb`. DH/OPW (`KukaIkSolver`) already diverges from the mesh. |
| Entity UUIDs / `ENTITY_ROBOT` | JSON path + `SceneNode.Name` is the identity. |
| `wobj0` / `tool0` in the post | We emit KRL `$BASE` / `$TOOL` / `TOOL_DATA[n]`. |
| Invert-tool-then-solve-flange as a *rewrite* | Equivalent to DLS-on-TCP iff `_tcpLocal` is rigid. Live mill/print already DLS on TCP. Optional later as an equivalence test, not a swap. |
| One `PrintingHead` ToolAsset swap for mill vs print | T12 pad XYZ is ~455 mm off `spindle.glb`. Unifying TCP sources re-breaks mill-beside-the-stick. |
| Rotary E1 as `attachedKinematicEntity → Robot` | LFAM 3 E1 spins the **work object** (BASE), not A7. |
| Hard-coded 6-axis *inside mill ops* as the bug | Mill/print already call the cell solver. The 6-bone lock is in `RobotFkController.JointNames`. Leave it until a 7th *robot* joint exists. |

## What we *do* adapt (the contract)

1. **Named frames**, queryable: `WORLD`, `ROBROOT`, `FLANGE` (`joint_6`), `TOOL` (taught `TOOL_DATA` XYZ+ABC), `CUTTER` (mill nose / spindle bit), `BASE` (print bed / rotary).
2. **TCP is a tool frame on the flange, not a 7th joint.** Already true. Make it a type, not a pile of `_tcpOffsetLocal` fields.
3. **Cell owns the chain.** Mill/print pass `TargetPose` in BASE + `SolveRecipe`. They do not pick `_tcpLocal` themselves.
4. **External axes are typed**, not “another PrintingHead axis.”
5. **Tool convention is data**, not `if (KrlIndex == 12)` scattered through `ViewportView.axaml.cs`.

## Current vs target (one picture)

```
TODAY (implicit)                         TARGET (named, cell-owned)
SceneRoot mm                             WORLD
  LFAM 3_Robot  T(ROBROOT)                 ROBROOT
    GLB × GltfToScene                        joints 1–6  (unchanged GLB FK)
      joint_6  FlangeNode                      FLANGE
        GLB "tcp"  (unused mill)               (keep node; never call it TOOL)
        Staubli / Tool_*                       mesh only
        _tcpOffsetLocal  (fields)              TOOL  = taught XYZ+ABC  (not a mesh)
        mill AABB nose     (ad-hoc)            CUTTER = mill-local collet
        TcpFrameMatrix overlay                 overlay samples TOOL or CUTTER or world+Z
  RotaryBed / bed                          BASE  (E1 rotates BASE, not ROBROOT)
  LFAM 1 rail rewrite of robot T           RAIL  translates ROBROOT
```

Invariant to keep: `TCP_scene = _tcpLocal × World(joint_6)` with `_tcpLocal` a **pure translation** in GLTF metres. Taught ABC is orientation-only and never baked into `_tcpLocal`.

## Landmines (read before coding)

- `CellEnvironmentBuilder.FlangeTcpName = "SpindleBitTCP"` is a rotation-only child under flange. Overlay lime stick is `SpindleBitCylinder.NodeName = "__SpindleBitCylinder"` parented to **SceneRoot**. Do not merge these.
- T12 mill IK **position** = mill-local AABB (`TryMillLocalCollet`), **orientation** = pad `TOOL_DATA[12]` ABC. Triad = path cartesian, **world +Z**. Three different frames.
- `ToolAxisConvention` already exists and is **display/triad only** — “Does not change TOOL_DATA.” Do not overload it as mill IK convention.
- `KukaIkSolver.Solve` (analytic, invert-then-flange) is **not** the live path. Comments already say its flange ≠ GLTF flange.
- 15 known test failures: `docs/KNOWN-TEST-FAILURES.md`. Do not treat those as the regression baseline.
- After any stash/test run: `dotnet build src/MassiveSlicer.App/MassiveSlicer.App.csproj --no-incremental`.

## Files likely to change

| Role | Path |
|---|---|
| New types | `src/MassiveSlicer.Core/Kinematics/CellFrame.cs` (new) |
| New types | `src/MassiveSlicer.Core/Kinematics/ToolKinematicsSpec.cs` (new) |
| New types | `src/MassiveSlicer.Core/Kinematics/SolveRecipe.cs` (new) |
| New facade | `src/MassiveSlicer.Viewport/FK/CellKinematics.cs` (new) |
| Tests | `src/MassiveSlicer.Tests/CellFrameTest.cs`, `ToolKinematicsSpecTest.cs`, `CellKinematicsSolveTest.cs` (new) |
| Existing tests that must stay green | `Lfam3MillIkPathTest`, `T12HolderYawTest`, `Lfam3ToolheadGlbTest`, `GltfNumericalIkSolverRailTest`, `RailE1PlannerTest`, `Lfam3MillingConfigTest` |
| Wire | `ViewportView.axaml.cs` (`ResolveIkTcpLocal`, `RebuildIkSolver`, `ScrubIkForNode`, `SyncTcpReadout`) — **targeted**, not a file rewrite |
| Wire | `ToolpathFeasibilityEvaluator.cs` mill vs print branch |
| Console dump | `ConsoleCommandRegistry.cs` |
| JSON (optional, later) | `ToolCellConfig` in `CellConfig.cs` + `lfam3.json` — only if a new field is required; prefer deriving spec from existing `krlIndex` / name |
| Docs | `docs/CODE-MAP.md`, `memory.md` (changelog), this plan |

`ViewportView.axaml.cs` is ~178k tokens. Grep section labels (`Scrub IK`, `TCP readout`, `LFAM tool TCP`). Do not read the whole file.

---

### Task 1: Named frame types (no scene yet)

**Objective:** Core has a `CellFrameKind` + `CellFrame` value type so later code cannot say “TCP” without saying which one.

**Files:**
- Create: `src/MassiveSlicer.Core/Kinematics/CellFrame.cs`
- Test: `src/MassiveSlicer.Tests/CellFrameTest.cs`

**Step 1: Write failing test**

```csharp
using MassiveSlicer.Core.Kinematics;
using Xunit;

public class CellFrameTest
{
    [Fact]
    public void Kinds_are_distinct_and_named()
    {
        Assert.NotEqual(CellFrameKind.Tool, CellFrameKind.Cutter);
        Assert.NotEqual(CellFrameKind.Flange, CellFrameKind.GlbTcp);
        Assert.Equal("TOOL_DATA", CellFrameKind.Tool.DumpName());
        Assert.Equal("CUTTER", CellFrameKind.Cutter.DumpName());
        Assert.Equal("FLANGE", CellFrameKind.Flange.DumpName());
        Assert.Equal("GLB_tcp", CellFrameKind.GlbTcp.DumpName());
        Assert.Equal("BASE", CellFrameKind.Base.DumpName());
        Assert.Equal("ROBROOT", CellFrameKind.Robroot.DumpName());
    }

    [Fact]
    public void Frame_stores_origin_mm_and_optional_abc()
    {
        var f = new CellFrame(
            CellFrameKind.Tool,
            OriginMm: new System.Numerics.Vector3(-78.4f, 325.2f, 637.4f),
            AbcDeg: new System.Numerics.Vector3(103.7f, -43.7f, 40.5f));
        Assert.Equal(CellFrameKind.Tool, f.Kind);
        Assert.True(f.HasOrientation);
    }
}
```

**Step 2: Run test to verify failure**

Run: `dotnet test src/MassiveSlicer.Tests/MassiveSlicer.Tests.csproj --filter FullyQualifiedName~CellFrameTest -v q`

Expected: FAIL — `CellFrameKind` not defined.

**Step 3: Write minimal implementation**

```csharp
using System.Numerics;

namespace MassiveSlicer.Core.Kinematics;

public enum CellFrameKind
{
    World,
    Robroot,
    Flange,
    GlbTcp,
    Tool,
    Cutter,
    Base,
}

public readonly record struct CellFrame(
    CellFrameKind Kind,
    Vector3 OriginMm,
    Vector3? AbcDeg = null)
{
    public bool HasOrientation => AbcDeg is { } a
        && (MathF.Abs(a.X) + MathF.Abs(a.Y) + MathF.Abs(a.Z) > 1e-3f);
}

public static class CellFrameKindNames
{
    public static string DumpName(this CellFrameKind kind) => kind switch
    {
        CellFrameKind.World   => "WORLD",
        CellFrameKind.Robroot => "ROBROOT",
        CellFrameKind.Flange  => "FLANGE",
        CellFrameKind.GlbTcp  => "GLB_tcp",
        CellFrameKind.Tool    => "TOOL_DATA",
        CellFrameKind.Cutter  => "CUTTER",
        CellFrameKind.Base    => "BASE",
        _ => kind.ToString(),
    };
}
```

**Step 4: Run test to verify pass**

Same `dotnet test` filter. Expected: PASS.

**Step 5: Commit**

```bash
git add src/MassiveSlicer.Core/Kinematics/CellFrame.cs src/MassiveSlicer.Tests/CellFrameTest.cs
git commit -m "feat(cell): named kinematic frames (TOOL vs CUTTER vs FLANGE)"
```

---

### Task 2: Tool kinematics spec (data, not Viewport ifs)

**Objective:** One record describes how a mounted tool maps flange → IK TCP and which overlay to draw. Derived from existing `ToolCellConfig` — no JSON schema change yet.

**Files:**
- Create: `src/MassiveSlicer.Core/Kinematics/ToolKinematicsSpec.cs`
- Test: `src/MassiveSlicer.Tests/ToolKinematicsSpecTest.cs`
- Read: `src/MassiveSlicer.Core/Models/CellConfig.cs` (`ToolCellConfig`)

**Step 1: Write failing test** (load `assets/cells/LFAM3/lfam3.json` via `CellLoader` like `Lfam3MillingConfigTest`)

```csharp
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Kinematics;
using Xunit;

public class ToolKinematicsSpecTest
{
    static string Lfam3()
    {
        var cells = AssetPaths.FindCellsDirectory()
            ?? throw new DirectoryNotFoundException("assets/cells");
        return Path.Combine(cells, "LFAM3", "lfam3.json");
    }

    [Fact]
    public void T12_uses_cutter_position_and_taught_abc()
    {
        var cell = CellLoader.Load(Lfam3());
        var t12 = cell.EffectiveTools.First(t => t.KrlIndex == 12);
        var spec = ToolKinematicsSpec.FromTool(t12);
        Assert.Equal(IkTcpSource.MillCollet, spec.PositionSource);
        Assert.Equal(IkOrientSource.TaughtAbc, spec.OrientSource);
        Assert.Equal(TriadSource.WorldUpAtPath, spec.TriadSource);
        Assert.Equal(0f, spec.HolderYawDeg);
    }

    [Fact]
    public void Extruder_uses_taught_xyz_and_abc()
    {
        var cell = CellLoader.Load(Lfam3());
        var t1 = cell.EffectiveTools.First(t => t.KrlIndex == 1);
        var spec = ToolKinematicsSpec.FromTool(t1);
        Assert.Equal(IkTcpSource.TaughtXyz, spec.PositionSource);
        Assert.Equal(IkOrientSource.TaughtAbc, spec.OrientSource);
        Assert.Equal(TriadSource.ToolFrame, spec.TriadSource);
        Assert.Equal(90f, spec.HolderYawDeg);
    }

    [Fact]
    public void Spindle_no_bit_uses_cutter_if_present_else_taught()
    {
        var cell = CellLoader.Load(Lfam3());
        var t2 = cell.EffectiveTools.First(t => t.KrlIndex == 2);
        var spec = ToolKinematicsSpec.FromTool(t2);
        Assert.Equal(IkTcpSource.SpindleCutter, spec.PositionSource);
    }
}
```

**Step 2: Run** `dotnet test … --filter FullyQualifiedName~ToolKinematicsSpecTest`

Expected: FAIL — type missing.

**Step 3: Minimal implementation**

```csharp
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Kinematics;

public enum IkTcpSource
{
    TaughtXyz,      // TOOL_DATA XYZ → GLTF metres via existing roll map
    MillCollet,     // mill-local AABB nose (T12)
    SpindleCutter,  // SpindleBitTCP / cutter world → flange local
    Flange,         // identity (No Tool / T4)
}

public enum IkOrientSource
{
    Flange,
    TaughtAbc,
}

public enum TriadSource
{
    ToolFrame,        // taught TCP + ABC (print / scan)
    WorldUpAtPath,    // T12 Drive Cell 3D
    Flange,
}

public sealed record ToolKinematicsSpec(
    int KrlIndex,
    string Name,
    IkTcpSource PositionSource,
    IkOrientSource OrientSource,
    TriadSource TriadSource,
    float HolderYawDeg,
    float ToolFrameRollDeg,
    float TcpX, float TcpY, float TcpZ,
    float TcpA, float TcpB, float TcpC)
{
    public static ToolKinematicsSpec FromTool(ToolCellConfig t)
    {
        bool t12 = t.KrlIndex == 12
            || t.Name.Contains("Tool 12", StringComparison.OrdinalIgnoreCase);
        bool spindle = !t12
            && t.KrlIndex is 2 or 3 or 7 or 8 or 9 or 10
            || t.Name.Contains("Spindle", StringComparison.OrdinalIgnoreCase);
        bool noTool = t.KrlIndex == 4
            || t.Name.Equals("No Tool", StringComparison.OrdinalIgnoreCase);

        var pos = t12 ? IkTcpSource.MillCollet
                : noTool ? IkTcpSource.Flange
                : spindle ? IkTcpSource.SpindleCutter
                : IkTcpSource.TaughtXyz;

        return new ToolKinematicsSpec(
            t.KrlIndex, t.Name, pos,
            IkOrientSource.TaughtAbc,
            t12 ? TriadSource.WorldUpAtPath : TriadSource.ToolFrame,
            HolderYawDeg: t12 ? 0f : 90f,
            t.ToolFrameRoll, t.TcpX, t.TcpY, t.TcpZ, t.TcpA, t.TcpB, t.TcpC);
    }
}
```

Match today’s `IsKukaTool12` / `UsesSpindleCutterTriad` / `ToolHolderYaw` exactly. Do not “improve” T2 mill bits in this task.

**Step 4:** test PASS.

**Step 5: Commit** `feat(cell): ToolKinematicsSpec from TOOL_DATA without JSON change`

---

### Task 3: SolveRecipe (mill vs print policy as data)

**Objective:** The mill position-first vs print 6D split is a named recipe, not comments in `ScrubIkForNode`.

**Files:**
- Create: `src/MassiveSlicer.Core/Kinematics/SolveRecipe.cs`
- Test: `src/MassiveSlicer.Tests/SolveRecipeTest.cs`

**Step 1: Failing test**

```csharp
using MassiveSlicer.Core.Kinematics;
using Xunit;

public class SolveRecipeTest
{
    [Fact]
    public void Mill_is_position_first_then_optional_6d()
    {
        var r = SolveRecipe.Mill;
        Assert.True(r.PositionFirst);
        Assert.True(r.ThenOrient);
        Assert.False(r.RequireWorkspace);
        Assert.Equal(400, r.PositionMaxIter);
        Assert.Equal(120, r.OrientMaxIter);
        Assert.True(r.PreferNamedHomeSeed);
    }

    [Fact]
    public void Print_is_6d_from_current_pose()
    {
        var r = SolveRecipe.Print;
        Assert.False(r.PositionFirst);
        Assert.True(r.ThenOrient);
        Assert.Equal(300, r.OrientMaxIter);
        Assert.False(r.PreferNamedHomeSeed);
    }
}
```

**Step 3: Implementation** — copy the numbers from `ViewportView.axaml.cs` mill scrub (~15347) and print `Solve(..., targetRot, 300)`.

```csharp
namespace MassiveSlicer.Core.Kinematics;

public sealed record SolveRecipe(
    bool PositionFirst,
    bool ThenOrient,
    bool RequireWorkspace,
    int PositionMaxIter,
    int OrientMaxIter,
    bool PreferNamedHomeSeed)
{
    public static readonly SolveRecipe Mill = new(
        PositionFirst: true, ThenOrient: true, RequireWorkspace: false,
        PositionMaxIter: 400, OrientMaxIter: 120, PreferNamedHomeSeed: true);

    public static readonly SolveRecipe Print = new(
        PositionFirst: false, ThenOrient: true, RequireWorkspace: true,
        PositionMaxIter: 0, OrientMaxIter: 300, PreferNamedHomeSeed: false);
}
```

**Step 5: Commit** `feat(cell): mill vs print SolveRecipe constants`

---

### Task 4: Frame snapshot from a synthetic FK chain

**Objective:** Given a tiny scene graph (robot wrapper → GltfToScene → joint_6 → glb tcp), dump WORLD/ROBROOT/FLANGE/GLB_tcp origins without the full cell load.

**Files:**
- Create: `src/MassiveSlicer.Viewport/FK/CellFrameSnapshot.cs`
- Test: `src/MassiveSlicer.Tests/CellFrameSnapshotTest.cs`
- Read: `RobotFkController.cs`, `GltfLoader.GltfToScene`

**Step 1: Failing test** — build the same mini-chain as `Lfam3ToolheadGlbTest` (robot root `GltfToScene`, joint_6 with a millimetre pose). Assert:

- `Robroot` origin = wrapper translation
- `Flange` origin = `joint_6.WorldTransform.Row3`
- `GlbTcp` origin ≠ `Flange` when local T is nonzero
- `Tool` is **not** inferred from the GLB node (snapshot takes taught XYZ as an argument, or leaves Tool empty)

Do **not** parent a mill mesh in this test.

**Step 3: Implementation sketch**

```csharp
public sealed record CellFrameSnapshot(
    CellFrame World,      // identity / unused
    CellFrame Robroot,
    CellFrame Flange,
    CellFrame? GlbTcp,
    CellFrame? Tool,      // taught, flange-local mm + ABC — origin in WORLD after map
    CellFrame? Cutter,    // optional, filled by caller
    CellFrame? Base);

public static class CellFrameDump
{
    public static CellFrameSnapshot FromFk(
        SceneNode robotWrapper,
        RobotFkController fk,
        Vector3 robrootMm,
        ToolKinematicsSpec? spec,
        Vector3? cutterWorldMm,
        Vector3? baseOriginMm);
}
```

World math: row-vector, mm. Tool world origin = flange world + taught XYZ mapped with the **existing** `RebuildFrameMatrices` GLTF↔KUKA map. If that map is trapped in `ViewportView`, extract a static helper in this task or the next — do not duplicate a third roll formula.

Prefer extracting `ToolFrameMaps` from `ViewportView.RebuildFrameMatrices` / `ResolveIkTcpLocal` into `src/MassiveSlicer.Viewport/FK/ToolFrameMaps.cs` **as a move**, same numbers.

**Step 5: Commit** `feat(cell): dump ROBROOT/FLANGE/GLB_tcp from FK chain`

---

### Task 5: Extract ToolFrameMaps (no behavior change)

**Objective:** One place converts taught mm + roll → `_tcpLocal` metres. `ResolveIkTcpLocal` and the dump both call it.

**Files:**
- Create: `src/MassiveSlicer.Viewport/FK/ToolFrameMaps.cs`
- Modify: `ViewportView.axaml.cs` `ResolveIkTcpLocal`, `RebuildFrameMatrices` — call the helper
- Test: `src/MassiveSlicer.Tests/ToolFrameMapsTest.cs` — T12 mill collet path is **not** this helper; taught XYZ path is.

**Golden:** For roll=0, taught `(tx,ty,tz)` mm → GLTF metres `(tx, tz, -ty)/1000` (see `Lfam3MillIkPathTest` line ~50). HV roll uses the existing `_gltfToKukaLocal` matrix. Copy, don’t invent.

**Verification:** `Lfam3MillIkPathTest` + `T12HolderYawTest` still pass.

**Commit:** `refactor(cell): extract ToolFrameMaps from ViewportView`

---

### Task 6: CellKinematics.Solve — wrap existing DLS

**Objective:** One method mill and print both call. Internals still `GltfNumericalIkSolver.Solve`. No Jacobian rewrite.

**Files:**
- Create: `src/MassiveSlicer.Viewport/FK/CellKinematics.cs`
- Test: `src/MassiveSlicer.Tests/CellKinematicsSolveTest.cs` — can reuse mill-start seed + a bed TCP from `Lfam3MillIkPathTest` (position-only, requireWorkspace false)

**API:**

```csharp
public sealed class CellKinematics
{
    public CellKinematics(GltfNumericalIkSolver solver, ToolKinematicsSpec spec);

    public float[]? Solve(
        Vector3 targetRobrootMm,
        float[] seed,
        SolveRecipe recipe,
        (Vector3 r0, Vector3 r1, Vector3 r2)? targetRot,
        float[]? namedHomeSeed = null);
}
```

Behavior:

- `recipe.PositionFirst`: `Solve(pos, posSeed)` then optional `Solve(pos, result, rot)`.
- Else: `Solve(pos, seed, rot)`.
- `PreferNamedHomeSeed` uses `namedHomeSeed` (Mill Start) as `posSeed` when length ≥ 6.

Copy the mill/print branches from `ScrubIkForNode` (~15345–15359) and `ToolpathFeasibilityEvaluator` (~205–217) **verbatim**.

**Commit:** `feat(cell): CellKinematics.Solve wraps mill/print DLS recipes`

---

### Task 7: Wire mill/print to CellKinematics (behavior-neutral)

**Objective:** `ScrubIkForNode` and `ToolpathFeasibilityEvaluator` call `CellKinematics.Solve`. Same seeds, same tolerances, same T12 collet `_tcpLocal` (still built in `RebuildIkSolver` via spec.PositionSource).

**Files:**
- Modify: `ViewportView.axaml.cs` (~15341–15359)
- Modify: `ToolpathFeasibilityEvaluator.cs` (~205–217)
- Modify: `RebuildIkSolver` / `ResolveIkTcpLocal` to switch on `ToolKinematicsSpec.PositionSource` instead of `IsKukaTool12()` / `UsesSpindleCutterTriad()` — **same branches**, just named.

**Do not** change mill triad (world +Z) in this task.

**Tests:** `Lfam3MillIkPathTest`, `GltfNumericalIkSolverRailTest`. If a number moves, stop and diff — this task is a move, not a fix.

**Commit:** `refactor(cell): mill/print IK go through CellKinematics`

---

### Task 8: Console `frames` dump

**Objective:** On a live cell, one command prints the UUID-less chain the proposal wanted: origins of ROBROOT, FLANGE, GLB_tcp, TOOL_DATA world, CUTTER world, BASE.

**Files:**
- Modify: `src/MassiveSlicer.App/Console/ConsoleCommandRegistry.cs`
- Test: a unit test that formats a `CellFrameSnapshot` to text (no GL). Example:

```
ROBROOT   0.0  0.0  1000.0
FLANGE    … …
GLB_tcp   … …   (unused mill)
TOOL_DATA … …  ABC=103.7 -43.7 40.5   (pad, not cutter)
CUTTER    … …  (mill nose)
BASE      2135.4  -52.5  916.3
delta(TOOL_DATA, CUTTER) = … mm
```

Register as `frames` next to `cal-check`.

**Commit:** `feat(cell): frames console dump ROBROOT→CUTTER`

---

### Task 9: External axis kind (document + type only)

**Objective:** Stop the next person hanging rotary E1 on the robot. Add `ExternalAxisKind { None, RobotRail, RotaryWork }` on a small helper; `CellKinematics` does not solve E1.

**Files:**
- Create: `src/MassiveSlicer.Core/Kinematics/ExternalAxisKind.cs`
- Test: rail cell → `RobotRail`, LFAM3 → `RotaryWork`, a cell with neither → `None`
- Docs only in CODE-MAP: “E1 rail rewrites ROBROOT; E1 rotary rewrites BASE. IK is always 6-axis in the current ROBROOT.”

No planner changes. `RailE1Planner` stays the rail owner.

**Commit:** `feat(cell): ExternalAxisKind rail vs rotary work object`

---

### Task 10: CODE-MAP + memory.md

**Objective:** Future agents find the contract without rereading ViewportView.

**Files:**
- Modify: `docs/CODE-MAP.md` — add a “Cell frames / IK” row pointing at `CellFrame.cs`, `ToolKinematicsSpec.cs`, `CellKinematics.cs`, `GltfNumericalIkSolver.cs`, `RobotFkController.cs`
- Modify: `memory.md` session changelog (newest first):

```
### 2026-09-02 — Cell frames contract (feature/Improved-Cell)
- Symptom: proposal wanted RL Jacobian + flange-first IK + wobj0
- Cause: that stack is the wrong product
- Fix: named frames + ToolKinematicsSpec + CellKinematics wrapper; GLB FK kept
- Key files: CellFrame.cs, ToolKinematicsSpec.cs, CellKinematics.cs
```

Do **not** mark ROADMAP “Built” until mill scrub is print-verified.

**Commit:** `docs: cell frame contract (no rl::mdl)`

---

### Task 11 (optional, later — not this slice): invert-then-flange equivalence

Only after Tasks 1–8 are green on a machine:

- Add `GltfNumericalIkSolver.SolveFlangeFirst` that does `flangeTarget = TCP × inv(_tcpLocal)` then DLS on `ComputeJoint6` translation.
- Test: same mill-start seed, same bed point, position error vs TCP-DLS < 1 mm.

If they disagree, **keep TCP-DLS**. Do not switch live path.

---

## Tests / validation (every wiring task)

```bash
dotnet test src/MassiveSlicer.Tests/MassiveSlicer.Tests.csproj \
  --filter "FullyQualifiedName~CellFrameTest|FullyQualifiedName~ToolKinematicsSpecTest|FullyQualifiedName~SolveRecipeTest|FullyQualifiedName~Lfam3MillIkPathTest|FullyQualifiedName~T12HolderYawTest|FullyQualifiedName~Lfam3ToolheadGlbTest|FullyQualifiedName~GltfNumericalIkSolverRailTest|FullyQualifiedName~RailE1PlannerTest|FullyQualifiedName~Lfam3MillingConfigTest"
```

Compare any extra failures to `docs/KNOWN-TEST-FAILURES.md`.

Shop check after Task 7 (human): LFAM 3, Tool 12, mill path — green stick on path, mill nose on stick, not 0.5 m beside it. Print T1 triad still on taught nozzle.

## Effort (honest)

| Tasks 1–4, 9–10 | ~1–2 days | types + dump + docs |
| Task 5–7 | ~2–4 days | extract maps + wrap solver; regression risk is T12 |
| Task 8 | ~half day | console |
| Task 11 | skip until proven | |
| Full RL port | do not | 3–6 months, wrong product |

## Risks

- Touching `ResolveIkTcpLocal` without the mill-collet test green = mill-beside-stick again.
- Renaming `SpindleBitTCP` while the overlay still searches that string.
- “Flange-first” comments in new code that do not match DLS-on-TCP — keep the comment: *IK solves joints so a fixed flange-local TCP lands on the target.*
- Dirty tree on this checkout is **unrelated** WIP. Do not commit it with these tasks. Commit only the files listed.

## Open questions (do not block Tasks 1–8)

1. Persist `IkTcpSource` in `lfam3.json` later, or keep deriving from `krlIndex`? Default: derive.
2. LFAM 1/2 tool parented to SceneRoot vs flange — out of scope. Spec still applies; parenting is a later visual cleanup.
3. Dev Mode live ABC vs triad world+Z — leave as-is; dump both TOOL ABC and triad source.

## First implementer action

Stay on `feature/Improved-Cell`. Do not merge `main` dirty files. Start Task 1. Push at each commit.
