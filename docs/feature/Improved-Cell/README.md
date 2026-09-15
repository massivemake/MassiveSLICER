# Improved Cell — what we are integrating, and why

**Branch:** `feature/Improved-Cell`  
**Status:** contract + plan only. No kinematics code on this branch yet (`414bd21` was the first docs commit).  
**Not for `main` until:** LFAM 3 mill scrub (nose on the path, not 0.5 m beside it) and a print export are verified on the machine.

Task-by-task TDD lives in [PLAN.md](PLAN.md). This file is the product decision.

---

## One-sentence decision

We are **not** porting the Robotics Library / RAPID cell architecture. We **are** taking its *contract* — named frames, cell-owned kinematics, TCP is a tool on the flange, mill and print do not own the arm — and implementing that contract in C# on the GLB FK we already trust.

---

## Why this exists

A change proposal asked MassiveSLICER to stop assuming a hard-coded 6-axis mill chain, treat the robot as an `rl::mdl` model, invert the tool, Jacobian-solve the **flange**, hang tracks / heads / work objects off entity UUIDs, and post `wobj0` / `tool0`.

That document describes a different product (ABB + Robotics Library + cartesian machines). Dropping it onto this repo would replace a working KUKA + GLB stack with a solver that **does not match the arm we draw**.

The shop bugs it is trying to prevent are real, though:

| What actually broke | Why a named contract helps |
|---|---|
| Mill over the table, green stick on the path, mill **0.5 m beside it** | Pad `TOOL_DATA[12]` XYZ (−78 / 325 / 637) is ~455 mm off `spindle.glb`. IK used “TCP” without saying *which* TCP. |
| Path all red, pose mill-down, arm parked at Mill Start | 6D T12 ABC + mill Z-up from `0/−90/90/0/45/105` returns null. Mill must be **position-first**. |
| Overlay / triad / IK / pad all called “TCP” | Four different frames. Mixing any two looks like an IK bug. |
| “IK never solves TCP; it solves the flange” | True as **kinematics**. False as the **live loop**. People “fix” the wrong solver. |
| LFAM 1 rail vs LFAM 3 rotary both called E1 | Rail translates **ROBROOT**. Rotary spins **BASE**. Treating both as A7 is wrong. |

So we integrate the **vocabulary and ownership**, not the C++ scene graph.

---

## What we have today (the baseline)

Live path (scrub, drag, reachability, mill/print feasibility):

1. Cell JSON loads a robot GLB (`LFAM3Robot.glb`) into `SceneNode`s.
2. `RobotFkController` turns A1–A6 into `joint_1`…`joint_6`.
3. `GltfNumericalIkSolver` is damped least-squares on **TCP**:
   - `TCP_scene = _tcpLocal × World(joint_6)`
   - `_tcpLocal` is a **pure translation** in GLTF metres (rebuilt on mount)
   - Taught ABC is orientation-only and is **never** baked into `_tcpLocal`
4. Target is TCP in ROBROOT millimetres. Jacobian columns come from `ComputeTcpPosScene`.
5. Mill vs print already share that solver. They do **not** share the tool frame:
   - **Print T1:** position = taught XYZ, orientation = taught ABC, triad = tool frame.
   - **Mill T12:** position = mill-local AABB nose (`TryMillLocalCollet`), orientation = pad ABC, triad = path cartesian **world +Z**. Position-only from Mill Start, then optional 6D.

Unused for the viewport: analytic `KukaIkSolver` (OPW). That one *does* invert the tool and solve the flange. Comments already say its flange **does not** match the GLTF flange. We will not make it the live path.

External axes already exist, but as two different mechanisms:

- **LFAM 1 E1** — linear rail. `RailE1Planner` moves ROBROOT, then 6-axis IK in the carriage frame.
- **LFAM 3 E1** — rotary table. Work-object / BASE spin. Not a seventh robot joint.

The 6-bone lock is in `RobotFkController.JointNames`, **not** inside mill ops. Mill/print already call the cell solver. The proposal overstated mill-op coupling.

---

## What we will integrate

Five pieces. Each is a thin C# type or a move of existing code, not a new kinematics library.

### 1. Named frames

**Integrate:** `CellFrameKind` + `CellFrame` so code cannot say “TCP” without saying which one.

| Name in Slicer | Meaning | Today it is buried as |
|---|---|---|
| `WORLD` | SceneRoot millimetres | implicit |
| `ROBROOT` | Robot wrapper origin | `Robot.WorldPosition` / live rail rewrite |
| `FLANGE` | `joint_6` | `FlangeNode` |
| `GLB_tcp` | Bone named `tcp` under the robot GLB | unused for mill (~125 mm bone Y) |
| `TOOL` / `TOOL_DATA` | Taught pendant XYZ + ABC | `_tcpOffsetLocal` + N-menu Dev Mode |
| `CUTTER` | Mill nose / collet | `TryMillLocalCollet` AABB |
| `BASE` | Print bed / rotary | `Bed.BaseMarkerWorld` / `$BASE` |

**Why:** Every recent mill bug was two of these getting the same name. A dump that prints `delta(TOOL_DATA, CUTTER)` makes the 455 mm lie visible instead of “IK is wrong.”

**Not:** entity UUIDs. JSON path + `SceneNode.Name` is identity.

### 2. Tool kinematics spec (data, not Viewport `if`s)

**Integrate:** `ToolKinematicsSpec.FromTool(ToolCellConfig)` derived from existing cell JSON (`krlIndex`, TCP, `toolFrameRoll`). No schema change in the first slice.

Per mounted tool it states:

- **Position source** — taught XYZ, mill collet, spindle cutter, or flange
- **Orientation source** — taught ABC vs flange
- **Triad source** — tool frame vs world-up-at-path vs flange
- **Holder yaw** — T12 is Ry = 0; HV / other GLB tools stay Ry(+90°)

**Why:** `IsKukaTool12()`, `UsesSpindleCutterTriad()`, and `ToolHolderYaw` are the same policy copied through `ViewportView.axaml.cs`. One record is the policy. Mill vs print **must** keep different sources — unifying them onto pad XYZ is how mill-beside-the-stick happens.

**Not:** one `PrintingHead` that “just swaps ToolAsset.” T12 pad XYZ is not the cutter.

### 3. Solve recipes (mill vs print as data)

**Integrate:** `SolveRecipe.Mill` and `SolveRecipe.Print` with the numbers already in `ScrubIkForNode` / `ToolpathFeasibilityEvaluator`.

| | Print | Mill T12 |
|---|---|---|
| First pass | 6D from current pose | Position-only, Mill Start seed `0/−90/90/0/45/105` |
| Then | — | Optional 6D (keep position if 6D is null) |
| Workspace reject | on | off (bed is outside a naive envelope from Mill Start) |
| Iters | 300 orient | 400 position, 120 orient |

**Why:** The mill 6D-first path parks the arm. That is a **recipe**, not a second IK library. Naming it stops the next change from “simplifying” mill back to print 6D.

**Not:** a Jacobian rewrite. Internals stay `GltfNumericalIkSolver.Solve`.

### 4. Cell-owned solve facade

**Integrate:** `CellKinematics.Solve(targetRobroot, seed, recipe, targetRot, namedHomeSeed)`.

Mill and print pass a **target pose in BASE/ROBROOT** and a recipe. They do not pick `_tcpLocal`. `_tcpLocal` is built from `ToolKinematicsSpec.PositionSource` when the tool mounts (`RebuildIkSolver`).

**Why:** This is the proposal’s useful invariant — *the cell owns the chain; ops query it* — without `rl::mdl`. One call site for scrub and batch feasibility so they cannot drift again.

**Not:** invert-tool-then-solve-flange as the live algorithm. DLS-on-TCP and invert-then-flange are the same **if** `_tcpLocal` is rigid. Live mill/print already differentiate TCP. An optional later test may prove equivalence; if they disagree, keep TCP-DLS.

### 5. Typed external axes + a `frames` dump

**Integrate:**

- `ExternalAxisKind { None, RobotRail, RotaryWork }` — documentation and a type, no planner rewrite.
- Console `frames` — print ROBROOT, FLANGE, GLB_tcp, TOOL_DATA world, CUTTER world, BASE, and `delta(TOOL_DATA, CUTTER)`.

**Why:** The proposal’s “dump the entity UUID chain and one solved sample” is the right **next shop step**, minus UUIDs. Rotary E1 must not grow a `getDepositionExternalAxis()` onto the robot. Rail stays `RailE1Planner`.

---

## What we will not integrate (and why)

| Proposal | Skip | Why |
|---|---|---|
| `rl::mdl::Model` + `JacobianInverseKinematics` | Yes | Viewport IK exists so joints match `LFAM3Robot.glb`. Analytic DH already diverges from the mesh. Native C++ dep, 3–6 months, every SRC at risk. |
| Invert TCP, Jacobian on flange, as a rewrite | Yes | Slogan, not a capability unlock. Live solver already DLS on TCP. |
| Entity UUIDs / `ENTITY_ROBOT` | Yes | We already have cell JSON + scene names. |
| Post `wobj0` / `tool0` | Yes | Wrong language. We emit `$BASE` / `$TOOL` / `TOOL_DATA[n]`. |
| Head = ToolAsset swap for mill and print | Yes | Different TCP sources on purpose. |
| Rotary / gantry as `attachedKinematicEntity → Robot` | Yes | LFAM 3 E1 is BASE motion, not A7. LFAM 1 E1 is ROBROOT motion. |
| Hard-coded 6-axis *inside mill ops* as the bug | N/A | Not the bug. Leave `joint_1…6` until a seventh **robot** joint exists. |

---

## How it maps onto files

| New | Role |
|---|---|
| `Core/Kinematics/CellFrame.cs` | Named frames |
| `Core/Kinematics/ToolKinematicsSpec.cs` | Per-tool IK/triad policy |
| `Core/Kinematics/SolveRecipe.cs` | Mill vs print DLS policy |
| `Core/Kinematics/ExternalAxisKind.cs` | Rail vs rotary |
| `Viewport/FK/CellKinematics.cs` | One Solve() mill and print call |
| `Viewport/FK/ToolFrameMaps.cs` | Taught mm + roll → `_tcpLocal` metres (extracted, not rewritten) |
| `Viewport/FK/CellFrameSnapshot.cs` | Dump for console / tests |

Wire (behavior-neutral): `ViewportView.axaml.cs` (`ResolveIkTcpLocal`, `RebuildIkSolver`, `ScrubIkForNode`), `ToolpathFeasibilityEvaluator.cs`, `ConsoleCommandRegistry.cs`. Targeted greps only — do not read ViewportView whole.

Must stay green: `Lfam3MillIkPathTest` (especially `Mill_collet_vs_tool_data_after_pos_ik`), `T12HolderYawTest`, `Lfam3ToolheadGlbTest`, `GltfNumericalIkSolverRailTest`, `RailE1PlannerTest`, `Lfam3MillingConfigTest`.

---

## Invariant we will not break

> IK solves the six joints so a **fixed flange-local TCP** lands on the target. The tool is not a seventh joint. Viewport DLS differentiates TCP; it does not invert the tool and solve the flange. Analytic `KukaIkSolver` does invert-then-flange, and is not the live path.

Plus the T12 split, which is shop-correct:

- IK **position** = CUTTER (mill nose)
- IK **orientation** = TOOL_DATA ABC
- Overlay triad on a mill path = world +Z at the path cartesian
- Overlay FLANGE = A6 when TCP axes are on
- Lime stick = SceneRoot overlay, not a mill child
- `SpindleBitTCP` = rotation-only empty under flange for coupler parenting — **not** TOOL_DATA XYZ

---

## Effort and risk

| Slice | Time | Risk |
|---|---|---|
| Types, dump, docs (PLAN tasks 1–4, 8–10) | 1–2 days | Low |
| Extract maps + wrap solver (tasks 5–7) | 2–4 days | **T12** — one wrong `_tcpLocal` and mill is beside the stick again |
| Invert-then-flange equivalence (task 11) | later / skip | Do not switch live path if they disagree |
| Full RL port | do not | 3–6 months, wrong product |

This is a **big** change in AGENTS.md terms (robot motion). Work stays on this branch. Push at every stopping point. Do not mix the unrelated dirty working tree into these commits.

---

## What “done” looks like

1. Console `frames` on a live LFAM 3 cell prints ROBROOT → FLANGE → TOOL_DATA → CUTTER → BASE, and TOOL vs CUTTER is ~455 mm, not ~0.
2. Mill T12 scrub: mill nose on the green stick, arm not parked at Mill Start.
3. Print T1 triad still on the taught nozzle.
4. Mill and print feasibility both call `CellKinematics.Solve` with different recipes, same GLB solver.
5. No `rl::mdl`, no `wobj0`, no JSON schema break unless a later task proves a field is required.

Until (2) is machine-verified, this branch is **not print-verified**. Do not merge to `main`.
