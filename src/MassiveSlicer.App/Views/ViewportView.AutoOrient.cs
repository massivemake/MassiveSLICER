#pragma warning disable CA1416  // Windows-only app
using MassiveSlicer.App.Enums;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Viewport.Collision;
using MassiveSlicer.Viewport.Validation;
using MassiveSlicer.ViewModels;
using NMatrix = System.Numerics.Matrix4x4;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;
using TkMatrix4 = OpenTK.Mathematics.Matrix4;
using TkQuaternion = OpenTK.Mathematics.Quaternion;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace MassiveSlicer.App.Views;

public partial class ViewportView
{
    /// <summary>Spin resolution about the vertical, degrees.</summary>
    private const float AutoOrientSpinStepDeg = 15f;

    /// <summary>Candidate spots per bed axis.</summary>
    private const int AutoOrientGridPerAxis = 7;

    /// <summary>Moves IK-checked per candidate in the first, every-candidate pass.</summary>
    private const int AutoOrientCoarseSamples = 48;

    /// <summary>Moves IK-checked per candidate in the second pass (best few only).</summary>
    private const int AutoOrientFineSamples = 360;

    /// <summary>Candidates carried from the coarse pass to the fine pass.</summary>
    private const int AutoOrientFineKeep = 24;

    /// <summary>Full sweeps (every move, repair, collisions) tried before giving up.</summary>
    private const int AutoOrientMaxFullSweeps = 4;

    /// <summary>
    /// Auto Orient: finds a spot on the bed, and a spin about the vertical, where the robot can
    /// print every move with no unreachable move, no residual singularity and no predicted
    /// collision — then turns and slides the part there. It never tilts the part.
    /// </summary>
    /// <remarks>
    /// Spinning about the vertical and sliding cannot lift any point or change which face is
    /// down, so a part that is flat, flipped or laid on a side stays exactly that way. The old
    /// version also tilted parts to reduce overhang; that ignored planar printing rules and could
    /// land a part a fraction of a degree off flat, so it is gone.
    ///
    /// Speed comes from never re-slicing. The part's current toolpath (or one slice when it has
    /// none) is checked at every candidate pose through a transform, because a spin and a slide
    /// move every bead rigidly. Three passes, each on fewer candidates:
    /// every candidate on a small sample of moves, the best few on a larger sample, then the
    /// full <see cref="ToolpathFeasibilityEvaluator"/> sweep — the same verdict live validation
    /// gives — best first, until one passes. Ranking is fewest unreachable samples, then the most
    /// room to spare (joint limits and wrist singularity), then the smallest move.
    ///
    /// Rail: with E1 motion on, every candidate is planned with the same rail planner export
    /// uses, inside the rail's limits and travel allowance; with it off, the rail stays home.
    ///
    /// The toolpath is re-sliced by the normal pipeline after the part moves. Seam and infill
    /// placement can depend on world position, so the final path can differ slightly from the
    /// rigidly moved one that was checked; live validation re-checks it as usual.
    /// </remarks>
    private async Task RunAutoOrientAsync(ViewportViewModel vm)
    {
        if (vm.AdditiveSettings is not { } add || add.IsAutoOrientRunning) return;

        var item = vm.ResolveActivePrintObjectItem()
                   ?? vm.FindUserMeshOutlinerItem(_renderer.SelectedNode);
        if (item is null)
        {
            SetSliceStatus(vm, "Auto orient: select a mesh first.", isError: true);
            return;
        }

        var snapshots = CollectMeshSnapshots(item, requireVisible: false);
        if (snapshots.Count == 0)
        {
            SetSliceStatus(vm, "Auto orient: mesh has no geometry.", isError: true);
            return;
        }

        var solver = _ikSolver;
        var robot  = vm.Robot;
        if (solver is null || robot is null)
        {
            SetSliceStatus(vm, "Auto orient: robot kinematics are not loaded.", isError: true);
            return;
        }

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        add.IsAutoOrientRunning = true;
        add.AutoOrientProgressPercent = 0;
        add.AutoOrientStatusDetail = "Preparing…";
        SetSliceStatus(vm, "Auto orient: preparing…");
        try
        {
            var node = item.Node;
            var cell = vm.ActiveCell;

            // ── The toolpath to test: the part's own slice, or one slice now ─────────
            // Always a copy: the full sweep writes rail and nozzle-spin values onto the moves
            // it tests, and those must never leak into the live toolpath export reads.
            Toolpath toolpath;
            NVec3 origin;
            TkMatrix4 wt;
            if (_toolpathByNode.TryGetValue(node, out var live) && live.Layers.Count > 0)
            {
                toolpath = await Task.Run(() => CloneToolpath(live));
                _toolpathOriginByNode.TryGetValue(node, out origin);
                wt = node.WorldTransform;
            }
            else
            {
                add.AutoOrientStatusDetail = "Slicing…";
                var world = await Task.Run(() => WorldSnapshots(snapshots));
                (toolpath, _, _) = await ComputeToolpathAsync(world, SliceMethod.Planar, BuildSliceSettings(add));
                origin = NVec3.Zero;
                wt = TkMatrix4.Identity;
            }
            add.AutoOrientProgressPercent = 10;

            int totalMoves = 0;
            foreach (var layer in toolpath.Layers) totalMoves += layer.Moves.Count;
            if (totalMoves == 0)
            {
                SetSliceStatus(vm, "Auto orient: the part slices to nothing.", isError: true);
                return;
            }

            // Live captures (UI thread; immutable for the rest of the run).
            RefreshIkSceneKinematics();
            var robroot        = GetLiveRobrootWorldPos();
            var collisionWorld = BuildOrGetCollisionWorld();
            var chainRootColl  = _fkController is { } fk
                ? CollisionModelExtractor.ToNumericsMatrix(fk.LiveChainRootTransform())
                : NMatrix.Identity;
            var seed = new float[]
            {
                (float)robot.A1, (float)robot.A2, (float)robot.A3,
                (float)robot.A4, (float)robot.A5, (float)robot.A6,
            };
            var joints   = cell?.Robot.Joints is { Count: >= 6 } j ? j : null;
            bool e1Motion = add.E1MotionEnabled && cell?.RobotRail is not null;
            float homeE1  = (float)robot.E1;
            var homeWorld = cell is not null
                ? new NVec3(cell.Robot.WorldPosition.X, cell.Robot.WorldPosition.Y, cell.Robot.WorldPosition.Z)
                : new NVec3(robroot.X, robroot.Y, robroot.Z);
            var bed = cell?.Bed;
            NVec2 bedCenter = default;
            if (cell is { Bed: { } bedCfg })
            {
                var bc = bedCfg.ImportSurfaceCenter(cell.Robot.WorldPosition);
                bedCenter = new NVec2(bc.X, bc.Y);
            }
            float offA = (float)add.ToolheadA, offB = (float)add.ToolheadB, offC = (float)add.ToolheadC;

            // ── Samples, footprint and candidates ─────────────────────────────────────
            var cache = BuildScrubCache(toolpath);
            var (samples, footprint, pivot) = await Task.Run(
                () => SamplePart(toolpath, cache, origin, wt, AutoOrientFineSamples));
            var candidates = PlacementSearch.Generate(
                footprint, pivot, bed, bedCenter, AutoOrientSpinStepDeg, AutoOrientGridPerAxis);

            var ctx = new PlacementPrescreen.Context(
                solver, samples, offA, offB, offC, seed, joints, robroot,
                e1Motion, cell?.RobotRail, homeWorld, homeE1,
                (float)add.E1YPlusMm, (float)add.E1YMinusMm);

            // ── Pass 1: every candidate, a small sample ───────────────────────────────
            add.AutoOrientStatusDetail = $"Checking {candidates.Count} spins and spots…";
            SetSliceStatus(vm, $"Auto orient: checking {candidates.Count} spins and spots…");
            int coarseStride = Math.Max(1, samples.Length / AutoOrientCoarseSamples);
            var coarse = await Task.Run(() =>
            {
                var scores = new PlacementSearch.Score[candidates.Count];
                Parallel.For(0, candidates.Count, i => scores[i] = PlacementPrescreen.Evaluate(
                    ctx, PlacementSearch.Transform(candidates[i], pivot), coarseStride));
                return candidates.Select((c, i) => (c, s: scores[i])).ToList();
            });
            coarse.Sort(PlacementSearch.Compare);
            add.AutoOrientProgressPercent = 35;

            // ── Pass 2: the best few, a larger sample ─────────────────────────────────
            var shortlist = coarse.Take(AutoOrientFineKeep).Select(t => t.c).ToList();
            if (!shortlist.Contains(candidates[0])) shortlist.Add(candidates[0]);   // always weigh "leave it"
            var fine = await Task.Run(() =>
            {
                var scores = new PlacementSearch.Score[shortlist.Count];
                Parallel.For(0, shortlist.Count, i => scores[i] = PlacementPrescreen.Evaluate(
                    ctx, PlacementSearch.Transform(shortlist[i], pivot)));
                return shortlist.Select((c, i) => (c, s: scores[i])).ToList();
            });
            fine.Sort(PlacementSearch.Compare);
            add.AutoOrientProgressPercent = 50;
            LogToConsole($"[orient] {candidates.Count} candidates, {samples.Length} sample moves; " +
                         $"best sampled: {Describe(fine[0].c, pivot)} margin {fine[0].s.MarginDeg:0.#}° " +
                         $"unreachable {fine[0].s.Unreachable} ({elapsed.Elapsed.TotalSeconds:0.0} s)");

            // ── Pass 3: full sweep, best first, until one passes ──────────────────────
            (PlacementSearch.Candidate c, int total, int fail, int sing, int coll)? winner = null, closest = null;
            int sweeps = 0;
            foreach (var (cand, _) in fine.Take(AutoOrientMaxFullSweeps))
            {
                sweeps++;
                add.AutoOrientStatusDetail = $"Full check {sweeps}/{Math.Min(AutoOrientMaxFullSweeps, fine.Count)}…";
                add.AutoOrientProgressPercent = 50 + 45f * (sweeps - 1) / AutoOrientMaxFullSweeps;
                var wtCand = wt * CollisionModelExtractor.ToOpenTkMatrix(PlacementSearch.Transform(cand, pivot));
                var verdict = await Task.Run(() =>
                {
                    if (e1Motion && cell is not null)
                        PlanRailE1ForExport(toolpath, cell, add, origin, wtCand, homeE1);
                    else
                        foreach (var layer in toolpath.Layers)
                            foreach (var mv in layer.Moves)
                                mv.E1Mm = float.NaN;

                    return ToolpathFeasibilityEvaluator.Evaluate(new ToolpathFeasibilityEvaluator.Input(
                        Solver:             solver,
                        Toolpath:           toolpath,
                        Cache:              cache,
                        WorldTransform:     wtCand,
                        Origin:             origin,
                        OffsetADeg:         offA,
                        OffsetBDeg:         offB,
                        OffsetCDeg:         offC,
                        SeedKrl:            seed,
                        E1Motion:           e1Motion,
                        Rail:               cell?.RobotRail,
                        HomeWorld:          homeWorld,
                        HomeE1:             homeE1,
                        PrintMmS:           (float)add.PrintSpeed,
                        TravelMmS:          (float)add.TravelSpeed,
                        WipeMmS:            (float)add.WipeSpeed,
                        ApoCvelFrac:        (float)(add.ApoCvel / 100.0),
                        World:              collisionWorld,
                        ChainRootColl:      chainRootColl,
                        WorldTransformColl: CollisionModelExtractor.ToNumericsMatrix(wtCand),
                        OriginColl:         origin,
                        BeadWidthColl:      (float)add.BeadWidth,
                        Robroot:            robroot,
                        Joints:             joints), CancellationToken.None);
                });
                if (verdict is null) continue;

                int total = verdict.Reachable.Length, fail = 0, sing = 0, coll = 0;
                for (int i = 0; i < total; i++)
                {
                    if (!verdict.Reachable[i]) fail++;
                    if (verdict.Singularity[i]) sing++;
                    if (verdict.Collision is { } c && c[i]) coll++;
                }
                LogToConsole($"[orient] full check {Describe(cand, pivot)}: unreachable={fail} " +
                             $"singularity={sing} collisions={coll} of {total} ({elapsed.Elapsed.TotalSeconds:0.0} s)");

                var result = (cand, total, fail, sing, coll);
                if (closest is null || fail + sing + coll < closest.Value.fail + closest.Value.sing + closest.Value.coll)
                    closest = result;
                if (fail == 0 && sing == 0 && coll == 0) { winner = result; break; }
            }

            if (winner is not { } win)
            {
                string near = closest is { } cl
                    ? $" Closest: {Describe(cl.c, pivot)} — {cl.fail:N0} unreachable, {cl.sing:N0} singularity-risk, " +
                      $"{cl.coll:N0} predicted collisions of {cl.total:N0} moves."
                    : $" Closest sampled: {Describe(fine[0].c, pivot)} — {fine[0].s.Unreachable} of {samples.Length} sample moves out of reach.";
                SetSliceStatus(vm, "Auto orient: no spin and spot on the bed prints every move cleanly — " +
                                   "keeping the current placement." + near, isError: true);
                ScheduleClearSliceStatus(vm);
                return;
            }

            if (win.c.IsCurrentPose)
            {
                SetSliceStatus(vm, $"Auto orient: the current placement is already the best spot — " +
                                   $"all {win.total:N0} moves reachable, no singularity, no predicted collision.");
                ScheduleClearSliceStatus(vm);
            }
            else
            {
                // ── Apply: spin about the vertical, slide. Z is never touched. ─────────
                var before = node.LocalTransform;
                SpinNodeAboutWorldVertical(node, pivot, win.c.SpinDeg);
                if (win.c.SlideMm > 0f)
                    TranslateNodeWorld(node, new TkVector3(win.c.Dx, win.c.Dy, 0f));
                // Same follow-through as a typed move: the part's toolpath goes with it (it is
                // the exact path just checked, moved rigidly), undo covers both, and the robot
                // check re-runs at the new spot instead of showing the old spot's verdict.
                MirrorTypedTransformDelta(vm, node, before);
                RecordTransformUndo(vm, node, before, node.LocalTransform, "Auto Orient");
                vm.NotifyRenderNeeded();
                OnTransformApplied(vm);

                SetSliceStatus(vm, $"Auto orient: {Describe(win.c, pivot)} — all {win.total:N0} moves reachable, " +
                                   "no singularity, no predicted collision.");
                ScheduleClearSliceStatus(vm);
            }
            LogToConsole($"[orient] chose {Describe(win.c, pivot)} after {candidates.Count} candidates " +
                         $"and {sweeps} full check(s)");

            elapsed.Stop();   // the time reported is the search, not this display hold
            add.AutoOrientProgressPercent = 100;
            add.AutoOrientStatusDetail = "Auto orientation Done";
            await Task.Delay(900);
        }
        catch (Exception ex)
        {
            SetSliceStatus(vm, $"Auto orient failed: {ex.Message}", isError: true);
            System.Console.Error.WriteLine($"[orient] {ex}");
        }
        finally
        {
            add.IsAutoOrientRunning = false;
            LogToConsole($"[orient] finished in {elapsed.Elapsed.TotalSeconds:0.0} s");
        }
    }

    /// <summary>"turned 30°, moved to (x, y)" in plain words for the status line.</summary>
    private static string Describe(PlacementSearch.Candidate c, NVec2 pivot)
    {
        if (c.IsCurrentPose) return "left where it is";
        float spin = PlacementSearch.Wrap(c.SpinDeg);
        string turn = spin == 0f ? "" : $"turned {spin:0}°";
        string move = c.SlideMm < 1f ? "" : $"moved to ({pivot.X + c.Dx:0}, {pivot.Y + c.Dy:0})";
        return turn.Length > 0 && move.Length > 0 ? $"{turn}, {move}" : turn + move;
    }

    /// <summary>
    /// Turns <paramref name="node"/> by <paramref name="spinDeg"/> about the world vertical line
    /// through <paramref name="pivot"/>. A placement-bearing node gets the turn written straight
    /// into its stored rotation (<see cref="Viewport.Scene.NodeTransform.RotatedAbout"/>), so no
    /// decomposition can add a sliver of tilt; a matrix-driven node gets the exact matrix product.
    /// </summary>
    internal static void SpinNodeAboutWorldVertical(Viewport.Scene.SceneNode node, NVec2 pivot, float spinDeg)
    {
        if (spinDeg == 0f) return;
        var parent = node.Parent?.WorldTransform ?? TkMatrix4.Identity;
        var invParent = TkMatrix4.Identity;
        if (MathF.Abs(parent.Determinant) > 1e-12f)
            TkMatrix4.Invert(parent, out invParent);

        float rad = spinDeg * MathF.PI / 180f;
        var pivotWorld = new TkVector3(pivot.X, pivot.Y, 0f);

        if (node.Placement is { } placement)
        {
            var pivotParent = TkVector3.TransformPosition(pivotWorld, invParent);
            var axisParent  = TkVector3.Normalize(TransformDir(TkVector3.UnitZ, invParent));
            node.SetPlacement(placement.RotatedAbout(pivotParent, TkQuaternion.FromAxisAngle(axisParent, rad)));
            return;
        }

        var spin = TkMatrix4.CreateTranslation(-pivotWorld)
                 * TkMatrix4.CreateRotationZ(rad)
                 * TkMatrix4.CreateTranslation(pivotWorld);
        node.LocalTransform = node.LocalTransform * parent * spin * invParent;
    }

    /// <summary>
    /// Sample moves (world space, current pose) for the robot pre-check, the XY outline of the
    /// whole toolpath, and the spin pivot (the outline's bounding-box centre).
    /// </summary>
    /// <remarks>
    /// The samples are an even stride through the toolpath plus every move that sits on the
    /// outline — the outermost points are where reach runs out first — and the outline of the
    /// top layer, where the arm is highest. Kept in toolpath order so each IK solve seeds from
    /// its predecessor.
    /// </remarks>
    private static ((NVec3 pos, NVec3 normal)[] samples, List<NVec2> footprint, NVec2 pivot) SamplePart(
        Toolpath toolpath, (NVec3 pos, NVec3 normal)[] cache, NVec3 origin, TkMatrix4 wt, int strideTarget)
    {
        int total = cache.Length - 1;
        var world = new NVec3[total];
        var normal = new NVec3[total];
        var wtN = CollisionModelExtractor.ToNumericsMatrix(wt);
        for (int i = 0; i < total; i++)
        {
            var (p, n) = cache[i + 1];
            world[i] = NVec3.Transform(p - origin, wtN);
            normal[i] = NVec3.TransformNormal(n, wtN);
        }

        static List<int> HullIndices(NVec3[] pts, int from, int to)
        {
            var byPoint = new Dictionary<NVec2, int>();
            for (int i = from; i < to; i++) byPoint.TryAdd(new NVec2(pts[i].X, pts[i].Y), i);
            return PlacementSearch.Hull(byPoint.Keys).Select(h => byPoint[h]).ToList();
        }

        var pick = new SortedSet<int>();
        int stride = Math.Max(1, total / Math.Max(1, strideTarget));
        for (int i = 0; i < total; i += stride) pick.Add(i);
        foreach (int i in HullIndices(world, 0, total)) pick.Add(i);
        int topStart = total - (toolpath.Layers.Count > 0 ? toolpath.Layers[^1].Moves.Count : 0);
        foreach (int i in HullIndices(world, Math.Clamp(topStart, 0, total), total)) pick.Add(i);

        var footprint = PlacementSearch.Hull(world.Select(p => new NVec2(p.X, p.Y)));
        var min = new NVec2(float.MaxValue);
        var max = new NVec2(float.MinValue);
        foreach (var p in footprint) { min = NVec2.Min(min, p); max = NVec2.Max(max, p); }

        return (pick.Select(i => (world[i], normal[i])).ToArray(), footprint, (min + max) * 0.5f);
    }

    /// <summary>Mesh snapshots moved into world space (identity node transform), for slicing.</summary>
    private static List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)> WorldSnapshots(
        IReadOnlyList<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)> snapshots)
    {
        var result = new List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)>(snapshots.Count);
        foreach (var (positions, indices, world) in snapshots)
        {
            var pts = new TkVector3[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                pts[i] = TkVector3.TransformPosition(positions[i], world);
            result.Add((pts, indices, TkMatrix4.Identity));
        }
        return result;
    }

    /// <summary>Move-by-move copy, so a throwaway check never writes onto the live toolpath.</summary>
    private static Toolpath CloneToolpath(Toolpath source)
    {
        var copy = new Toolpath();
        foreach (var layer in source.Layers)
        {
            var l = new ToolpathLayer(layer.Index, layer.Z) { PlaneNormal = layer.PlaneNormal, Height = layer.Height };
            foreach (var mv in layer.Moves) l.Moves.Add(mv with { });
            copy.Layers.Add(l);
        }
        return copy;
    }
}
