#pragma warning disable CA1416  // Windows-only app
using MassiveSlicer.App.Enums;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Viewport.Collision;
using MassiveSlicer.Viewport.Validation;
using MassiveSlicer.ViewModels;
using NMatrix = System.Numerics.Matrix4x4;
using NVec3 = System.Numerics.Vector3;
using TkMatrix4 = OpenTK.Mathematics.Matrix4;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace MassiveSlicer.App.Views;

public partial class ViewportView
{
    /// <summary>
    /// Bed positions tried per rotation candidate (bed centre plus alternates). Every extra one is
    /// a full re-slice and IK/collision sweep, so this stays small on purpose.
    /// </summary>
    private const int MaxAutoOrientPlacements = 5;

    /// <summary>Below this the part did not meaningfully move, so don't claim it did.</summary>
    private const float AutoOrientMoveEpsMm = 1f;

    /// <summary>
    /// Auto Orient: searches full 3D rotations of the selected part AND a handful of positions on
    /// the bed for the pose with the least overhang risk that the robot can actually print, then
    /// rotates and moves the part into it.
    /// </summary>
    /// <remarks>
    /// Two stages, because overhang risk is cheap to score and robot feasibility is not.
    /// Stage 1 ranks orientations geometrically (<see cref="OrientationOptimizer"/>), dropping only
    /// the ones whose footprint is too big for the bed at any position. Stage 2 walks that list
    /// best-first and, for each rotation, tries a short list of bed placements
    /// (<see cref="OrientationOptimizer.SuggestPlacements"/>, bed centre first): it slices a
    /// throwaway copy of the part's geometry in that (rotation, position) pose and runs the real
    /// validation sweep over it (<see cref="ToolpathFeasibilityEvaluator"/>) — the first combination
    /// with zero unreachable moves, zero predicted collisions AND zero residual singularity-risk
    /// moves wins, and the search stops there. A combination that fails any of the three is
    /// rejected outright: printing an unreachable, colliding, or singular pose faults mid-print,
    /// which is worse than the overhangs Auto Orient was asked to fix, so "best available" is never
    /// good enough here.
    ///
    /// Position is part of the search because it has to be. Rotating in place cannot fix a part
    /// parked somewhere the arm simply cannot cover — every orientation of it is unreachable for the
    /// same reason — and that is exactly the case a rotation-only search reported as "no orientation
    /// is feasible". Each combination costs a real re-slice plus a full IK/collision sweep, so the
    /// list of placements is kept deliberately short and the search is best-first, not exhaustive.
    ///
    /// Nothing touches the scene until a winner is found; candidate geometry and its toolpaths are
    /// plain arrays that are discarded after scoring, never attached to the outliner.
    ///
    /// v1 simplification: the rail (E1) is treated as parked at home for every candidate rather
    /// than re-planned per orientation, so a cell that depends on rail travel to reach the part may
    /// reject candidates a rail-aware plan would accept. Conservative in the safe direction.
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

        add.IsAutoOrientRunning = true;
        add.AutoOrientProgressPercent = 0;
        add.AutoOrientStatusDetail = "Searching orientations…";
        SetSliceStatus(vm, "Auto orient: searching orientations…");
        try
        {
            // ── Stage 1: geometric search over every "which face is down" ──────────
            var (soup, center) = await Task.Run(() =>
            {
                // Same world-space triangle soup the slicer consumes.
                var flatSoup = new List<NVec3[]>(snapshots.Count);
                var min = new NVec3(float.MaxValue);
                var max = new NVec3(float.MinValue);
                foreach (var (positions, indices, world) in snapshots)
                {
                    NVec3[] flat;
                    if (indices is null)
                    {
                        flat = new NVec3[positions.Length];
                        for (int i = 0; i < positions.Length; i++)
                            flat[i] = TransformPoint(positions[i], world);
                    }
                    else
                    {
                        flat = new NVec3[indices.Length];
                        for (int i = 0; i < indices.Length; i++)
                            flat[i] = TransformPoint(positions[indices[i]], world);
                    }
                    foreach (var p in flat)
                    {
                        min = NVec3.Min(min, p);
                        max = NVec3.Max(max, p);
                    }
                    flatSoup.Add(flat);
                }
                return (flatSoup, (min + max) * 0.5f);
            });

            var bed = vm.ActiveCell?.Bed;

            // Where the placement search is allowed to put the part. This is cell-layout math, so
            // it uses the cell config's DECLARED robot position — the same convention
            // ImportSurfaceFrame uses for where fresh imports land — not the live-jogged ROBROOT
            // capture below, which exists for IK and is a different thing entirely.
            (float x, float y)? bedCenter = null;
            if (vm.ActiveCell is { Bed: { } bedCfg } cellCfg)
            {
                var bc = bedCfg.ImportSurfaceCenter(cellCfg.Robot.WorldPosition);
                bedCenter = (bc.X, bc.Y);
            }

            var candidates = await Task.Run(
                () => OrientationOptimizer.FindCandidates(soup, bed, maxCandidates: 5));

            if (candidates.Count == 0)
            {
                SetSliceStatus(vm,
                    "Auto orient: no orientation both fits the bed and improves on the current one.",
                    isError: true);
                ScheduleClearSliceStatus(vm);
                return;
            }

            add.AutoOrientProgressPercent = 10;

            // ── Stage 2: robot feasibility, best-first ─────────────────────────────
            var solver = _ikSolver;
            var robot  = vm.Robot;
            if (solver is null || robot is null)
            {
                SetSliceStatus(vm, "Auto orient: robot kinematics are not loaded.", isError: true);
                return;
            }

            // Live captures (UI thread; immutable for the rest of the run). The collision world is
            // robot + fixture geometry, which no candidate orientation changes — build it once.
            RefreshIkSceneKinematics();
            var robroot        = GetLiveRobrootWorldPos();
            var collisionWorld = BuildOrGetCollisionWorld();
            var chainRootColl  = _fkController is { } fk
                ? CollisionModelExtractor.ToNumericsMatrix(fk.LiveChainRootTransform())
                : NMatrix.Identity;
            float bedZ   = _renderer.BedZ;
            float offA   = (float)add.ToolheadA;
            float offB   = (float)add.ToolheadB;
            float offC   = (float)add.ToolheadC;
            float bead   = (float)add.BeadWidth;
            float printMmS    = (float)add.PrintSpeed;
            float travelMmS   = (float)add.TravelSpeed;
            float wipeMmS     = (float)add.WipeSpeed;
            float apoCvelFrac = (float)(add.ApoCvel / 100.0);
            float homeE1      = (float)robot.E1;
            var   homeWorld   = new NVec3(robroot.X, robroot.Y, robroot.Z);
            var   seed        = new float[]
            {
                (float)robot.A1, (float)robot.A2, (float)robot.A3,
                (float)robot.A4, (float)robot.A5, (float)robot.A6,
            };
            // Decorative effects stay as configured: SliceSettings' properties are init-only, so
            // stripping them would mean duplicating BuildSliceSettings' whole initialiser, and
            // scoring the settings the operator will actually print with is the truer test anyway.
            var evalSettings = BuildSliceSettings(add);

            OrientationOptimizer.Candidate? winner = null;
            (float x, float y)? winPlacement = null;
            int winReach = 0, winTotal = 0, winSing = 0, winColl = 0;

            // Worst case for the progress bar: every rotation × every placement. Real runs almost
            // always finish on the first attempt or two, so the bar normally jumps to Done early —
            // preferable to a bar that claims to be nearly finished and then keeps going.
            int worstCaseAttempts = candidates.Count * (bedCenter is null ? 1 : MaxAutoOrientPlacements);
            int attempt = 0;

            for (int ci = 0; ci < candidates.Count && winner is null; ci++)
            {
                var cand = candidates[ci];

                // Placements for THIS rotation — the slack that's left over depends on how big its
                // own footprint is. Null entry = no cell bed to place against, so rotate in place
                // exactly as before.
                var placements = new List<(float x, float y)?>();
                if (bedCenter is { } bc)
                    foreach (var p in OrientationOptimizer.SuggestPlacements(
                                 bc, bed, cand.FootprintExtentX, cand.FootprintExtentY,
                                 maxPlacements: MaxAutoOrientPlacements))
                        placements.Add(p);
                else
                    placements.Add(null);

                for (int pi = 0; pi < placements.Count; pi++)
                {
                    var placement = placements[pi];
                    attempt++;

                    string progress = placements.Count > 1
                        ? $"Evaluating candidate {ci + 1}/{candidates.Count}, " +
                          $"position {pi + 1}/{placements.Count}…"
                        : $"Evaluating candidate {ci + 1}/{candidates.Count}…";
                    SetSliceStatus(vm,
                        $"Auto orient: {char.ToLowerInvariant(progress[0])}{progress[1..]}");
                    add.AutoOrientStatusDetail = progress;
                    add.AutoOrientProgressPercent =
                        10 + attempt / (float)Math.Max(1, worstCaseAttempts) * 80;

                    var candGeometry = await Task.Run(
                        () => BuildCandidateGeometry(soup, center, cand.Rotation, bedZ, placement));

                    var (evalToolpath, _, _) = await ComputeToolpathAsync(
                        candGeometry, SliceMethod.Planar, evalSettings);

                    var verdict = await Task.Run(() =>
                    {
                        // Rail parked at home for the whole evaluation (v1) — same bake the live
                        // validation applies when rail motion is off.
                        foreach (var layer in evalToolpath.Layers)
                            foreach (var mv in layer.Moves)
                                mv.E1Mm = float.NaN;

                        var input = new ToolpathFeasibilityEvaluator.Input(
                            Solver:             solver,
                            Toolpath:           evalToolpath,
                            Cache:              BuildScrubCache(evalToolpath),
                            // Candidate geometry was placed in final world coordinates before
                            // slicing, so its toolpath positions already ARE world positions.
                            WorldTransform:     TkMatrix4.Identity,
                            Origin:             NVec3.Zero,
                            OffsetADeg:         offA,
                            OffsetBDeg:         offB,
                            OffsetCDeg:         offC,
                            SeedKrl:            seed,
                            E1Motion:           false,
                            Rail:               null,
                            HomeWorld:          homeWorld,
                            HomeE1:             homeE1,
                            PrintMmS:           printMmS,
                            TravelMmS:          travelMmS,
                            WipeMmS:            wipeMmS,
                            ApoCvelFrac:        apoCvelFrac,
                            World:              collisionWorld,
                            ChainRootColl:      chainRootColl,
                            WorldTransformColl: NMatrix.Identity,
                            OriginColl:         NVec3.Zero,
                            BeadWidthColl:      bead,
                            Robroot:            robroot);

                        return ToolpathFeasibilityEvaluator.Evaluate(input, CancellationToken.None);
                    });

                    if (verdict is null) continue;   // empty slice — nothing to judge

                    int total = verdict.Reachable.Length;
                    int fail = 0, sing = 0, coll = 0;
                    for (int i = 0; i < total; i++)
                    {
                        if (!verdict.Reachable[i]) fail++;
                        if (verdict.Singularity[i]) sing++;
                        if (verdict.Collision is { } c && c[i]) coll++;
                    }

                    string posText = placement is { } pl ? $"({pl.x:0}, {pl.y:0})" : "in place";
                    System.Console.WriteLine(
                        $"[orient] candidate {ci + 1}/{candidates.Count} pos {pi + 1}/{placements.Count} " +
                        $"{posText}  risk {cand.RiskAfter * 100:0.##}%  " +
                        $"unreachable={fail}  singularity={sing}  collisions={coll}  moves={total}");

                    // Hard filter — a pose the robot cannot print cleanly is not a candidate at
                    // all. Singularity-risk moves join unreachable/collision here rather than
                    // being report-only: the evaluator already tried to repair them by spinning
                    // the (rotationally symmetric, so print-neutral) nozzle, so anything still
                    // flagged survived that repair attempt — a real residual risk, not noise.
                    if (fail > 0 || coll > 0 || sing > 0) continue;

                    // Rotations are risk-sorted ascending and placements run centre-first, so the
                    // first combination to pass is the best feasible one. Stop the whole search.
                    winner       = cand;
                    winPlacement = placement;
                    winReach     = total - fail;
                    winTotal     = total;
                    winSing      = sing;
                    winColl      = coll;
                    break;
                }
            }

            if (winner is null)
            {
                SetSliceStatus(vm,
                    $"Auto orient: {candidates.Count} candidate orientation(s) improve overhang but " +
                    "no orientation and position combination is fully reachable, collision-free, " +
                    "and singularity-clear — keeping the current placement.",
                    isError: true);
                ScheduleClearSliceStatus(vm);
                return;
            }

            // ── Apply the winner to the live node ─────────────────────────────────
            var node   = item.Node;
            var before = node.LocalTransform;
            {
                // Rotate about the part's own centre so it turns in place, then resettle on the bed.
                var parentWorld = node.Parent?.WorldTransform ?? TkMatrix4.Identity;
                var c   = new TkVector3(center.X, center.Y, center.Z);
                var rot = TkMatrix4.CreateTranslation(-c)
                        * CollisionModelExtractor.ToOpenTkMatrix(winner.Rotation)
                        * TkMatrix4.CreateTranslation(c);
                node.LocalTransform = node.WorldTransform * rot * parentWorld.Inverted();
            }
            DropNodeToBed(node, _renderer.BedZ);

            // Slide to the winning placement. The pose that was evaluated had its rotated footprint
            // centred on the target, so the live node has to land on that same reference point —
            // rotating about the ORIGINAL bbox centre does not move that centre, but it does move
            // the footprint centre, so the delta is measured from the rotated footprint centre, not
            // from `center`. Anything else applies a pose that was never the one validated.
            string movedText = "";
            if (winPlacement is { } win)
            {
                var fp = RotatedFootprintCenter(soup, center, winner.Rotation);
                float dx = win.x - fp.x, dy = win.y - fp.y;
                if (MathF.Abs(dx) > AutoOrientMoveEpsMm || MathF.Abs(dy) > AutoOrientMoveEpsMm)
                {
                    // Exact world slide: writing a composed matrix onto a placement-bearing node
                    // re-derives (and slightly rotates) the basis — see TranslateNodeWorld.
                    TranslateNodeWorld(node, new TkVector3(dx, dy, 0f));
                    movedText = $" · moved to ({win.x:0}, {win.y:0})";
                }
            }

            // One undo entry for the whole rotate + drop + slide, not three.
            RecordTransformUndo(vm, node, before, node.LocalTransform, "Auto Orient");
            vm.NotifyRenderNeeded();
            GlCanvas.RequestNextFrameRendering();

            SetSliceStatus(vm,
                $"Auto orient: overhang risk {winner.RiskBefore * 100:0.#}% → {winner.RiskAfter * 100:0.#}%, " +
                $"{winReach}/{winTotal} moves reachable, {winSing:N0} singularity-risk, " +
                $"{winColl:N0} predicted collisions, bed fit {winner.BedFitMarginPct:0}%{movedText}");
            ScheduleClearSliceStatus(vm);
            System.Console.WriteLine(
                $"[orient] applied  risk {winner.RiskBefore * 100:0.##}% -> {winner.RiskAfter * 100:0.##}%  " +
                $"bedFit={winner.BedFitMarginPct:0.#}%  reachable={winReach}/{winTotal}  " +
                $"singularity={winSing}  attempts={attempt}{movedText}");

            // Hold the finished bar on screen briefly — otherwise the busy overlay vanishes
            // the instant IsAutoOrientRunning flips off and "Done" is never actually seen.
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
        }
    }

    /// <summary>
    /// Throwaway evaluation geometry for one candidate pose: the part's world-space triangle soup
    /// rotated about <paramref name="center"/> into the candidate orientation, dropped so its lowest
    /// point rests on <paramref name="bedZ"/> — the same settling <see cref="DropNodeToBed"/> does
    /// for a live node, applied to plain point arrays instead — and slid in XY so its footprint sits
    /// centred on <paramref name="targetXY"/>.
    /// </summary>
    /// <param name="targetXY">
    /// World XY the rotated footprint's centre should land on, or null to leave the part where it
    /// is (no cell bed to place against).
    /// </param>
    /// <returns>Mesh snapshots in final world coordinates (identity node transform).</returns>
    private static List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)> BuildCandidateGeometry(
        IReadOnlyList<NVec3[]> soup, NVec3 center, NMatrix rotation, float bedZ,
        (float x, float y)? targetXY)
    {
        var rotated = new NVec3[soup.Count][];
        float minZ = float.MaxValue;
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int m = 0; m < soup.Count; m++)
        {
            var src = soup[m];
            var dst = new NVec3[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var p = NVec3.Transform(src[i] - center, rotation) + center;
                dst[i] = p;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            rotated[m] = dst;
        }

        float dz = minZ < float.MaxValue ? bedZ - minZ : 0f;
        float dx = 0f, dy = 0f;
        if (targetXY is { } t && minX < float.MaxValue)
        {
            dx = t.x - (minX + maxX) * 0.5f;
            dy = t.y - (minY + maxY) * 0.5f;
        }

        var result = new List<(TkVector3[] positions, uint[]? indices, TkMatrix4 world)>(soup.Count);
        foreach (var verts in rotated)
        {
            var tk = new TkVector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                tk[i] = new TkVector3(verts[i].X + dx, verts[i].Y + dy, verts[i].Z + dz);
            result.Add((tk, null, TkMatrix4.Identity));
        }
        return result;
    }

    /// <summary>
    /// XY centre of the part's footprint once <paramref name="rotation"/> has been applied about
    /// <paramref name="center"/> — the same reference point <see cref="BuildCandidateGeometry"/>
    /// parks on a placement target, so the live node can be slid to exactly the pose that was
    /// evaluated. Rotating about the original bbox centre leaves that centre where it was but does
    /// move the footprint centre, so the two are not interchangeable on an asymmetric part.
    /// </summary>
    private static (float x, float y) RotatedFootprintCenter(
        IReadOnlyList<NVec3[]> soup, NVec3 center, NMatrix rotation)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var src in soup)
            foreach (var v in src)
            {
                var p = NVec3.Transform(v - center, rotation) + center;
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

        return minX < float.MaxValue
            ? ((minX + maxX) * 0.5f, (minY + maxY) * 0.5f)
            : (center.X, center.Y);
    }
}
