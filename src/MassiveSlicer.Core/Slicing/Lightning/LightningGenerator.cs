using System.Numerics;
using Clipper2Lib;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Lightning;

/// <summary>
/// Realizes a layer's lightning / Formbound plan as toolpath moves.
///
/// Every tree's centerlines are inflated by half a bead into a closed "slit";
/// <c>Difference(region, slits)</c> notches the perimeter so the boundary path is
/// a continuous hairpin detour (single bead — one continuous line).
///
/// <para><b>Formbound Bridge</b> — radial fingers (mouth → tip).</para>
/// <para><b>Formbound Buttress</b> — T-morph: wall approach → horizontal support bar
/// (still single-bead dual-wall slits, not multi-bead fill).</para>
/// </summary>
public static class LightningGenerator
{
    public static void EmitLightning(
        List<List<Vector2>> fillPolys,
        LightningLayerPlan? plan,
        float z,
        ToolpathLayer layer,
        float beadWidth,
        float tipLoopRadius,
        Func<Vector2, Vector3>? project = null,
        Vector3? startFrom = null)
    {
        var region = LightningPlanner.ToPathsD(fillPolys, beadWidth);
        if (region.Count == 0) return;

        // Phantom-island veto (mesh truth, when the slicer supplied an oracle):
        // the planner already refuses to BRIDGE under parity phantoms; here the
        // stray wall itself is removed. A real contour's interior is solid AT its
        // own plane by definition of slicing; a phantom's is void. The dominant
        // outer is never touched (safety), and real islands (posts) probe solid.
        var solidAt = plan?.SolidAt;
        string? dumpDirEarly = Environment.GetEnvironmentVariable("MSL_LIGHTNING_DUMP");
        if (plan?.SolidAtPlane is { } solidAtPlane && region.Count > 1)
            RemovePhantomIslands(region, solidAtPlane, plan.SolidAt, beadWidth, dumpDirEarly, z);
        if (region.Count == 0) return;

        // Debug hook: set MSL_LIGHTNING_DUMP to a directory to dump each layer's
        // input contours and normalized region as plane-local polylines.
        string? dumpDir = Environment.GetEnvironmentVariable("MSL_LIGHTNING_DUMP");
        if (dumpDir is not null)
            DumpPaths(dumpDir, z, ("FILL", fillPolys.Select(poly =>
                    string.Join(";", poly.Select(pt => $"{pt.X:0.##},{pt.Y:0.##}")))),
                ("REGION", region.Select(path =>
                    string.Join(";", path.Select(pt => $"{pt.x:0.##},{pt.y:0.##}")))));


        int outerCount = CountOuters(region);
        var result = region;

        if (plan is not null && plan.Trees.Count > 0)
        {
            // Thin band just inside the region boundary. A healthy slit crosses it
            // exactly once (its mouth); a second crossing means the slit is punching
            // through a nearby thin wall (e.g. a narrow inlet channel) — the neck
            // guard can't see that because the walls stay connected around the
            // channel end, but the wall gets eaten. Those lineages are dropped.
            var boundaryBand = Clipper.Difference(
                region,
                Clipper.InflatePaths(region, -beadWidth * 0.6, JoinType.Miter, EndType.Polygon, 3.0),
                FillRule.NonZero);

            // Cut, then verify the perimeter held. Crowded lineages (converging
            // inherited fingers) can merge into a cut blob that swallows a stretch
            // of wall no single-tree guard can see; any uncovered boundary arc
            // longer than a finger mouth drops the lineages that caused it and
            // re-cuts. The perimeter always wins over infill.
            for (int attempt = 0; ; attempt++)
            {
                result = CutTrees(region, boundaryBand, plan, beadWidth, tipLoopRadius, outerCount);
                if (attempt >= 3) break;
                var offenders = PerimeterBreachTrees(region, result, plan, beadWidth, tipLoopRadius);
                if (offenders.Count == 0) break;
                foreach (var id in offenders) plan.DroppedTrees.Add(id);
            }
        }

        if (dumpDir is not null)
            DumpPaths(dumpDir, z, ("RESULT", result.Select(path =>
                string.Join(";", path.Select(pt => $"{pt.x:0.##},{pt.y:0.##}")))));

        // Single-bead walls (e.g. interior partitions modelled at exactly one bead
        // thick) collapse to near-zero area under the half-bead inset and vanish from
        // the region — but they are real printable geometry that shells mode draws.
        // Recover them as standalone loops, deduped against their double-shell twins
        // and against curves the region already covers.
        var walls = new PathsD();
        foreach (var poly in fillPolys)
        {
            if (poly.Count < 3) continue;
            var path = new PathD(poly.Count);
            foreach (var pt in poly) path.Add(new PointD(pt.X, pt.Y));
            double a = Math.Abs(Clipper.Area(path));
            double perim = 0;
            for (int i = 0; i < path.Count; i++)
            {
                var p0 = path[i];
                var p1 = path[(i + 1) % path.Count];
                perim += Math.Sqrt((p1.x - p0.x) * (p1.x - p0.x) + (p1.y - p0.y) * (p1.y - p0.y));
            }
            if (perim < beadWidth) continue;                       // speck
            if (a >= perim * beadWidth * 0.25) continue;           // has real area → in region
            bool covered = result.Any(r => LightningPlanner.LiesOnCurve(path, r, beadWidth * 0.6))
                        || walls.Any(w => LightningPlanner.LiesOnCurve(path, w, 0.3));
            if (covered) continue;
            // Mesh check: a real collapsed partition lies ON the part; a parity
            // phantom's synthetic closure chords hang in space. Require most of
            // the curve to probe solid before printing it.
            if (solidAt is not null)
            {
                int nS = Math.Min(path.Count, 9), hits = 0;
                for (int k = 0; k < nS; k++)
                {
                    var pt = path[(int)((long)k * path.Count / nS)];
                    if (solidAt(new Vector2((float)pt.x, (float)pt.y))) hits++;
                }
                if (hits * 10 < nS * 7) continue;   // < 70 % on-part → phantom
            }
            walls.Add(path);
        }
        if (walls.Count > 0)
            result.AddRange(walls);

        // Soften acute corners on the toolpath without filling finger notches:
        // vertex-wise fillet of each boundary polyline (min radius = bead width).
        result = FilletBoundaryCorners(result, beadWidth);

        EmitLoops(result, z, layer, project, plan, beadWidth, tipLoopRadius, startFrom);
    }

    /// <summary>
    /// Replace sharp polyline corners with circular arcs of radius ≥ bead width so
    /// the extruder never turns tighter than one bead. Operates on the boundary
    /// curves only — does not inflate the filled region (which would erase slits).
    /// </summary>
    private static PathsD FilletBoundaryCorners(PathsD paths, float beadWidth)
    {
        if (paths.Count == 0 || beadWidth < 0.1f) return paths;
        float r = beadWidth;
        var outp = new PathsD();
        foreach (var path in paths)
        {
            if (path.Count < 3) { outp.Add(path); continue; }
            var pts = new List<Vector2>(path.Count);
            foreach (var pt in path) pts.Add(new Vector2((float)pt.x, (float)pt.y));
            // Closed loop: fillet including wrap-around corner.
            var filleted = FilletClosedLoop(pts, r);
            if (filleted.Count < 3) { outp.Add(path); continue; }
            var np = new PathD(filleted.Count);
            foreach (var p in filleted) np.Add(new PointD(p.X, p.Y));
            outp.Add(np);
        }
        return outp;
    }

    private static List<Vector2> FilletClosedLoop(List<Vector2> pts, float radius)
    {
        int n = pts.Count;
        if (n < 3) return pts;
        var dst = new List<Vector2>();
        for (int i = 0; i < n; i++)
        {
            var a = pts[(i - 1 + n) % n];
            var b = pts[i];
            var c = pts[(i + 1) % n];
            var ba = a - b;
            var bc = c - b;
            float la = ba.Length();
            float lc = bc.Length();
            if (la < 1e-5f || lc < 1e-5f) { dst.Add(b); continue; }
            ba /= la; bc /= lc;
            float cos = Math.Clamp(Vector2.Dot(ba, bc), -1f, 1f);
            float ang = MathF.Acos(cos);
            float turn = MathF.PI - ang;
            // Nearly straight — keep vertex.
            if (turn < 0.2f || float.IsNaN(turn)) { dst.Add(b); continue; }
            // Only fillet corners tighter than ~ bead (turn angle large enough that
            // the inscribed radius would be < bead if cut short).
            float half = turn * 0.5f;
            float cut = radius / MathF.Max(MathF.Tan(half), 1e-3f);
            cut = MathF.Min(cut, MathF.Min(la, lc) * 0.45f);
            if (cut < radius * 0.2f) { dst.Add(b); continue; }

            var p0 = b + ba * cut;
            var p1 = b + bc * cut;
            var inDir = -ba;
            float crossIO = inDir.X * bc.Y - inDir.Y * bc.X;
            var leftN = new Vector2(-inDir.Y, inDir.X);
            var toCenter = crossIO >= 0 ? leftN : -leftN;
            var center = p0 + toCenter * radius;
            var v0 = p0 - center;
            var v1 = p1 - center;
            if (v0.LengthSquared() < 1e-8f || v1.LengthSquared() < 1e-8f) { dst.Add(b); continue; }
            float a0 = MathF.Atan2(v0.Y, v0.X);
            float a1 = MathF.Atan2(v1.Y, v1.X);
            float da = a1 - a0;
            if (crossIO >= 0) { while (da < 0) da += MathF.PI * 2f; }
            else { while (da > 0) da -= MathF.PI * 2f; }
            // Skip extremely long sweeps (would invert topology).
            if (MathF.Abs(da) > MathF.PI * 1.1f) { dst.Add(b); continue; }
            int segs = Math.Max(3, (int)MathF.Ceiling(MathF.Abs(da) / (MathF.PI / 8f)));
            for (int s = 0; s <= segs; s++)
            {
                float t = s / (float)segs;
                float angS = a0 + da * t;
                dst.Add(center + new Vector2(MathF.Cos(angS), MathF.Sin(angS)) * radius);
            }
        }
        return dst.Count >= 3 ? dst : pts;
    }

    /// <summary>Removes region islands whose interior is mesh-void AT the plane:
    /// parity phantoms from grazing cuts (a pocket-rim curve without its host
    /// wall). Outers are grouped with their holes; the dominant outer is never
    /// dropped; an island goes only when under 30 % of its eroded-interior probes
    /// read solid (grazing planes weave through the surface, so demand a clear
    /// majority verdict, not a single sample).</summary>
    private static void RemovePhantomIslands(PathsD region, Func<Vector2, bool> solidAt,
        Func<Vector2, bool>? solidNear, float beadWidth, string? dumpDir = null, float z = 0f)
    {
        // Split into outers and holes; associate each hole with its enclosing outer.
        var outers = new List<int>();
        var holes  = new List<int>();
        for (int i = 0; i < region.Count; i++)
            (Clipper.Area(region[i]) > 0 ? outers : holes).Add(i);
        if (outers.Count <= 1) return;

        int dominant = outers.OrderByDescending(i => Clipper.Area(region[i])).First();
        double dominantArea = Clipper.Area(region[dominant]);

        var toRemove = new HashSet<int>();
        foreach (int oi in outers)
        {
            if (oi == dominant) continue;
            // Only small features can be phantoms worth auto-dropping. Components
            // comparable to the dominant body are parity-composed real geometry
            // (open-shell half-loops) whose eroded boundary verts land on synthetic
            // chords through the cavity — no whole-island probe can judge them.
            if (Clipper.Area(region[oi]) > dominantArea * 0.25) continue;
            var component = new PathsD { region[oi] };
            foreach (int hi in holes)
                if (Clipper.PointInPolygon(region[hi][0], region[oi]) != PointInPolygonResult.IsOutside)
                    component.Add(region[hi]);

            // Erode so probes sit safely inside the claimed material, clear of the
            // boundary curve (which lies on the mesh surface even for phantoms).
            var inner = Clipper.InflatePaths(component, -beadWidth * 0.3, JoinType.Miter, EndType.Polygon, 3.0);
            if (inner.Count == 0 || inner[0].Count == 0) continue;   // sliver — leave it

            int solid = 0, near = 0, total = 0;
            int nS = Math.Min(inner[0].Count, 9);
            for (int k = 0; k < nS; k++)
            {
                var pt = inner[0][(int)((long)k * inner[0].Count / nS)];
                var v2 = new Vector2((float)pt.x, (float)pt.y);
                total++;
                if (solidAt(v2)) solid++;
                if (solidNear?.Invoke(v2) == true) near++;
            }
            if (dumpDir is not null)
            {
                var pts = string.Join(" ", Enumerable.Range(0, nS).Select(k =>
                {
                    var pt = inner[0][(int)((long)k * inner[0].Count / nS)];
                    var v2 = new Vector2((float)pt.x, (float)pt.y);
                    return $"({pt.x:0.#},{pt.y:0.#})={(solidAt(v2) ? 1 : 0)}";
                }));
                DumpPaths(dumpDir, z, ("VETO", [
                    $"outer={oi} area={Clipper.Area(region[oi]):0} atPlane={solid}/{total} nearPlane={near}/{total} pts: {pts}"]));
            }
            if (total == 0 || solid * 10 >= total * 3) continue;   // ≥30 % solid → real

            toRemove.Add(oi);
            foreach (int hi in holes)
                if (Clipper.PointInPolygon(region[hi][0], region[oi]) != PointInPolygonResult.IsOutside)
                    toRemove.Add(hi);
        }
        if (toRemove.Count == 0) return;
        var kept = new PathsD(region.Count - toRemove.Count);
        for (int i = 0; i < region.Count; i++)
            if (!toRemove.Contains(i)) kept.Add(region[i]);
        region.Clear();
        region.AddRange(kept);
    }

    /// <summary>Applies every live tree's slit to the region (notch interior fingers,
    /// union external fins), running the per-tree guards, then morphologically closes
    /// the cut so converging slits merge into one clean notch instead of leaving
    /// sub-bead slivers. Trees failing a guard join <see cref="LightningLayerPlan.DroppedTrees"/>.</summary>
    private static PathsD CutTrees(PathsD region, PathsD boundaryBand,
        LightningLayerPlan plan, float beadWidth, float tipLoopRadius, int outerCount)
    {
        var result = region;
        PathsD? envelope = null;   // part silhouette — cavity tubes may never leave it
        foreach (var tree in plan.Trees)
        {
                // A guard dropped this lineage at a lower layer — its support column
                // is gone, so printing the inherited finger here would leave it in
                // mid-air. (Emission runs bottom-up.)
                if (plan.DroppedTrees.Contains(tree.Id)) continue;

                var slit = BuildTreeSlit(tree, region, beadWidth, tipLoopRadius);
                if (slit.Count == 0) continue;   // newborn stub, too small this layer — not a drop

                // Cavity tubes stay INSIDE the closed mesh by construction: clip
                // against the filled silhouette so a mouth overlapping a thin wall
                // can never poke out the far side (union bead landing outside).
                if (tree.Cavity)
                {
                    envelope ??= LightningPlanner.BuildEnvelope(region);
                    slit = Clipper.Intersect(slit, envelope, FillRule.NonZero);
                    if (slit.Count == 0) continue;
                }

                // Bite guard (slit body only — tip discs may legitimately graze the
                // band when the loop radius exceeds the core inset).
                // Interior fingers MUST notch the perimeter; a slit with no band
                // intersection is a free-floating dual-wall island (floating diagonal).
                var body = tipLoopRadius > 0f
                    ? BuildTreeSlit(tree, region, beadWidth, 0f)
                    : slit;
                var bite = Clipper.Intersect(boundaryBand, body, FillRule.NonZero);
                if (!tree.External && !tree.Cavity)
                {
                    bool hasBite = false;
                    foreach (var p in bite)
                        if (Math.Abs(Clipper.Area(p)) > beadWidth * beadWidth * 0.05)
                        { hasBite = true; break; }
                    if (!hasBite) { plan.DroppedTrees.Add(tree.Id); continue; }
                }
                // Cavity tubes legitimately touch the band at BOTH ends (wall mouth +
                // island landing) — the multi-crossing rule is for interior slits.
                if (!tree.Cavity && CountOuters(bite) > 1)
                { plan.DroppedTrees.Add(tree.Id); continue; }

                PathsD candidate;
                if (tree.External || tree.Cavity)
                {
                    // Fin (outside the part) or cavity tube (inside a modeled void):
                    // ADD the bump instead of notching. The boundary detours around
                    // it — still one continuous loop.
                    candidate = Clipper.Union(result, slit, FillRule.NonZero);
                    candidate = Clipper.SimplifyPaths(candidate, 0.05, false);
                    // Sacrificial fins must not merge separate islands (unexpected
                    // topology change); cavity CONNECTORS exist to do exactly that.
                    if (tree.External && CountOuters(candidate) < outerCount)
                    { plan.DroppedTrees.Add(tree.Id); continue; }
                }
                else
                {
                    candidate = Clipper.Difference(result, slit, FillRule.NonZero);
                    candidate = Clipper.SimplifyPaths(candidate, 0.05, false);
                    // Guard: a slit that cuts clean across a neck would split the region
                    // into extra islands (adding travels) — drop that lineage.
                    if (CountOuters(candidate) > outerCount) { plan.DroppedTrees.Add(tree.Id); continue; }
                }

                // Guard: degenerate output (region consumed).
                if (candidate.Count == 0) { plan.DroppedTrees.Add(tree.Id); continue; }

                result = candidate;
                // Later guards must judge against CURRENT topology (a connector may
                // have legitimately merged islands).
                outerCount = CountOuters(result);
            }

            // Converging fingers (inherited lineages drifting together — common on
            // angled sweeps) can leave a sliver of region thinner than a bead between
            // their slits: two walls printed almost on top of each other = gross
            // over-extrusion. Morphologically CLOSE the cut area (dilate + erode) so
            // near-touching slits merge into one clean notch. Gaps bounded by the
            // part's real exterior only dilate from one side and survive untouched.
            if (!ReferenceEquals(result, region))
            {
                var cut = Clipper.Difference(region, result, FillRule.NonZero);
                if (cut.Count > 0)
                {
                    double closeR = beadWidth * 0.55;
                    var closed = Clipper.InflatePaths(cut, closeR, JoinType.Round, EndType.Polygon);
                    closed     = Clipper.InflatePaths(closed, -closeR, JoinType.Round, EndType.Polygon);
                    // Subtract from RESULT (not region): the notches are already cut
                    // (idempotent) and unioned external fins must survive the merge.
                    var merged = Clipper.Difference(result, closed, FillRule.NonZero);
                    merged     = Clipper.SimplifyPaths(merged, 0.05, false);
                    // The rounded dilate/erode leaves sub-millimetre lens fragments along
                    // the old sliver midline; anything smaller than one bead² is
                    // unprintable anyway — drop it before judging topology.
                    merged.RemoveAll(path => Math.Abs(Clipper.Area(path)) < beadWidth * beadWidth);
                    // Keep the per-tree guards' promises: adopt the merge only when it
                    // doesn't change the island topology.
                    if (merged.Count > 0 && CountOuters(merged) == CountOuters(result))
                        result = merged;
                }
            }

        return result;
    }

    /// <summary>Finds boundary stretches of <paramref name="region"/> that
    /// <paramref name="result"/> leaves without a wall. A healthy finger mouth
    /// uncovers ≲2 beads of boundary; anything longer means the cut swallowed real
    /// perimeter — returns the live trees whose slits touch those breaches.</summary>
    private static List<int> PerimeterBreachTrees(PathsD region, PathsD result,
        LightningLayerPlan plan, float beadWidth, float tipLoopRadius)
    {
        var offenders = new List<int>();
        if (ReferenceEquals(result, region)) return offenders;

        // Thin band along every region wall (outers and holes alike).
        double bandW = beadWidth * 0.5;
        var band = Clipper.Difference(region,
            Clipper.InflatePaths(region, -bandW, JoinType.Miter, EndType.Polygon, 3.0),
            FillRule.NonZero);
        if (band.Count == 0) return offenders;

        // Tube around the emitted walls — where printed bead actually lies. The
        // result loops are closed rings; re-close them explicitly and inflate as
        // open polylines so the tube follows the wall CURVE, not the wall area.
        var wallLines = new PathsD();
        foreach (var path in result)
        {
            if (path.Count < 2) continue;
            var open = new PathD(path) { path[0] };
            wallLines.Add(open);
        }
        var tube = Clipper.InflatePaths(wallLines, beadWidth * 0.9, JoinType.Round, EndType.Round);

        var uncovered = Clipper.Difference(band, tube, FillRule.NonZero);
        if (uncovered.Count == 0) return offenders;

        var breaches = new PathsD();
        foreach (var path in uncovered)
            if (Clipper.Area(path) > 3.0 * beadWidth * bandW) breaches.Add(path);
        if (breaches.Count == 0) return offenders;

        var nearBreach = Clipper.InflatePaths(breaches, beadWidth, JoinType.Round, EndType.Polygon);
        foreach (var tree in plan.Trees)
        {
            if (plan.DroppedTrees.Contains(tree.Id)) continue;
            var slit = BuildTreeSlit(tree, region, beadWidth, tipLoopRadius);
            if (slit.Count == 0) continue;
            if (Clipper.Intersect(nearBreach, slit, FillRule.NonZero).Count > 0)
                offenders.Add(tree.Id);
        }
        return offenders;
    }

    private static void DumpPaths(string dir, float z, params (string Tag, IEnumerable<string> Lines)[] groups)
    {
        using var fw = new StreamWriter(Path.Combine(dir, $"gen_z{z:0.0}.txt"), append: true);
        foreach (var (tag, lines) in groups)
            foreach (var line in lines)
                fw.WriteLine($"{tag}\t{line}");
    }

    /// <summary>Inflates one tree's centerlines (plus optional tip discs) into slit polygons.</summary>
    private static PathsD BuildTreeSlit(LightningTree tree, PathsD region, float beadWidth, float tipLoopRadius)
    {
        float halfBead = beadWidth * 0.5f;
        var openLines = new PathsD();

        foreach (var branch in tree.Branches)
        {
            var line = branch.Centerline;
            if (line.Count < 2) continue;
            if (ArcLength(line) < beadWidth * 0.1f) continue;    // degenerate only —
            // short stubs MUST print: they are the first layers of a growing finger,
            // and skipping them would leave the next layer's stub unsupported.

            var path = new PathD(line.Count + 1);

            // Extend the root of trunk branches past the boundary. Interior slits
            // must fully CROSS the perimeter to notch it (one bead); cavity/fin
            // tubes are UNIONED — they only need enough overlap to fuse with the
            // wall, and a full bead punches straight through thin walls (union
            // bead landing OUTSIDE the mesh on a 4 mm skin).
            if (branch.ParentBranch < 0)
            {
                var dir = line[0] - line[1];
                float dl = dir.Length();
                if (dl > 1e-4f)
                {
                    dir /= dl;
                    float extLen = tree.Cavity || tree.External
                        ? beadWidth * 0.45f
                        : beadWidth;
                    var ext = line[0] + dir * extLen;
                    path.Add(new PointD(ext.X, ext.Y));
                }
            }
            foreach (var pt in line) path.Add(new PointD(pt.X, pt.Y));
            openLines.Add(path);
        }
        if (openLines.Count == 0) return new PathsD();

        // Round joins + round ends: mouth and tip never form knife corners.
        // Centerlines should already be filleted to ≥ bead radius in the planner.
        var slit = Clipper.InflatePaths(openLines, halfBead, JoinType.Round, EndType.Round, 2.0);

        // Tip support pads: a disc at every leaf tip makes the boundary loop around it.
        if (tipLoopRadius > 0f)
        {
            var discs = new PathsD();
            foreach (var branch in tree.Branches)
            {
                bool isLeaf = true;
                for (int oj = 0; oj < tree.Branches.Count; oj++)
                    if (tree.Branches[oj].ParentBranch >= 0
                        && ReferenceEquals(tree.Branches[tree.Branches[oj].ParentBranch], branch))
                    { isLeaf = false; break; }
                if (!isLeaf || branch.Centerline.Count < 2) continue;
                discs.Add(Disc(branch.Centerline[^1], tipLoopRadius, 24));
            }
            if (discs.Count > 0)
                slit = Clipper.Union(Clipper.Union(slit, FillRule.NonZero), discs, FillRule.NonZero);
        }

        return slit;
    }

    /// <summary>Emits every result polygon as a closed extrude loop; separate polygons
    /// (islands, holes) are connected by travels exactly like shell printing.</summary>
    private static void EmitLoops(PathsD polys, float z, ToolpathLayer layer, Func<Vector2, Vector3>? project,
        LightningLayerPlan? plan = null, float beadWidth = 0f, float tipLoopRadius = 0f,
        Vector3? startFrom = null)
    {
        Vector3 P(Vector2 p) => project?.Invoke(p) ?? new Vector3(p.X, p.Y, z);

        // Segments hugging a finger centerline (or a tip pad) are tagged so the
        // viewport can show/hide the fingers as their own display layer.
        float tagRadius = beadWidth * 0.8f;
        bool IsLightningSeg(Vector2 a2, Vector2 b2)
        {
            if (plan is null || plan.Trees.Count == 0) return false;
            var mid = (a2 + b2) * 0.5f;
            foreach (var tree in plan.Trees)
            {
                if (plan.DroppedTrees.Contains(tree.Id)) continue;
                foreach (var branch in tree.Branches)
                {
                    var line = branch.Centerline;
                    for (int i = 1; i < line.Count; i++)
                        if (DistToSegmentSq(mid, line[i - 1], line[i]) < tagRadius * tagRadius)
                            return true;
                    if (tipLoopRadius > 0f && line.Count > 0)
                    {
                        float rr = tipLoopRadius + beadWidth * 0.8f;
                        if (Vector2.DistanceSquared(mid, line[^1]) < rr * rr)
                            return true;
                    }
                }
            }
            return false;
        }

        // Largest outer first, then by area descending — mirrors shell ordering.
        var ordered = polys
            .Where(p => p.Count >= 3)
            .OrderByDescending(p => Math.Abs(Clipper.Area(p)))
            .ToList();
        if (ordered.Count == 0) return;

        // Preferred perimeter start: paint bridge target (ColumnFoot) locked for the
        // whole stack, else live PaintColumn mouth. Keeps the seam on the buttress
        // instead of drifting into the notch and looking like a broken column.
        Vector2? seamPin = plan?.SeamPinXY;
        if (seamPin is null && plan is not null)
        {
            foreach (var t in plan.Trees)
            {
                if (plan.DroppedTrees.Contains(t.Id)) continue;
                if (!t.PaintColumn) continue;
                seamPin = t.Anchor;
                break;
            }
        }

        Vector3? runningEnd = layer.Moves.Count > 0 ? layer.Moves[^1].To : null;
        // Chain reference for GREEDY PICKS only: seeded by the previous layer's end
        // so the first loop starts where the nozzle already is. The first loop must
        // NOT emit an explicit entry travel from it — the slicer's layer connector
        // owns that move (and stitches it when short, keeping layers travel-free).
        Vector3? chainRef = runningEnd ?? startFrom;
        bool outerSeamPinned = false;

        // Travel-optimal emission: the largest outer prints first (stable seam
        // anchor, optionally pinned to the paint-bridge seam); every following
        // loop is chosen greedily as the NEAREST remaining one and entered at
        // its nearest vertex. Fixed area-descending order zig-zagged across the
        // part (left boom → right boom by size = full-wingspan travels).
        var pending = ordered;
        bool firstLoop = true;

        while (pending.Count > 0)
        {
            int pick = 0;
            int start = 0;
            // Seam pin forces the classic largest-outer-first start; otherwise the
            // greedy runs from the very first loop (seeded by the previous layer's
            // end), so each layer begins on whichever island the nozzle is already
            // next to and hops shrink to actual island gaps.
            bool pinnedFirst = firstLoop && seamPin is not null;
            if (!pinnedFirst && chainRef is { } sel)
            {
                float pd2 = float.MaxValue;
                for (int t = 0; t < pending.Count; t++)
                {
                    var cand = pending[t];
                    for (int i = 0; i < cand.Count; i++)
                    {
                        var w = P(new Vector2((float)cand[i].x, (float)cand[i].y));
                        float d2 = (w.X - sel.X) * (w.X - sel.X) + (w.Y - sel.Y) * (w.Y - sel.Y);
                        if (d2 < pd2) { pd2 = d2; pick = t; start = i; }
                    }
                }
            }
            var path = pending[pick];
            pending.RemoveAt(pick);
            if (Environment.GetEnvironmentVariable("MSL_EMIT_DEBUG") == "1" && ordered.Count > 1)
                System.Console.WriteLine(
                    $"[emit-order] z={z:0.#} pick={pick} start={start} first={firstLoop} " +
                    $"pendingLeft={pending.Count} re={(runningEnd is { } r0 ? $"({r0.X:0},{r0.Y:0})" : "null")}");
            firstLoop = false;

            // Outer loops: pin start to the bridge seam. Holes / secondary islands
            // keep the nearest-vertex entry chosen above so travel stays short.
            bool isOuter = Clipper.Area(path) > 0;
            if (isOuter && !outerSeamPinned && seamPin is { } pin)
            {
                float bd2 = float.MaxValue;
                for (int i = 0; i < path.Count; i++)
                {
                    float dx = (float)path[i].x - pin.X;
                    float dy = (float)path[i].y - pin.Y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < bd2) { bd2 = d2; start = i; }
                }
                outerSeamPinned = true;
            }
            else if (start == 0 && chainRef is { } re)
            {
                float bd2 = float.MaxValue;
                for (int i = 0; i < path.Count; i++)
                {
                    var w = P(new Vector2((float)path[i].x, (float)path[i].y));
                    float d2 = (w.X - re.X) * (w.X - re.X) + (w.Y - re.Y) * (w.Y - re.Y);
                    if (d2 < bd2) { bd2 = d2; start = i; }
                }
            }

            var first2 = new Vector2((float)path[start].x, (float)path[start].y);
            var first  = P(first2);
            int entryTravel = -1;
            if (runningEnd is { } prev && Vector3.Distance(prev, first) > 0.01f)
            {
                float gap = Vector3.Distance(prev, first);
                if (beadWidth > 0f && gap <= beadWidth * 3f)
                {
                    // Islands within a few beads merge into ONE continuous path:
                    // extrude a single nozzle-width bridge across the gap instead
                    // of lifting for a travel.
                    layer.Moves.Add(new ToolpathMove(prev, first, MoveKind.Extrude));
                }
                else
                {
                    entryTravel = layer.Moves.Count;
                    layer.Moves.Add(new ToolpathMove(prev, first, MoveKind.Travel));
                }
            }

            int extrudeStart = layer.Moves.Count;
            var cur  = first;
            var cur2 = first2;   // plane-local twin of cur — tagging must use plane space
            for (int k = 1; k <= path.Count; k++)
            {
                var idx  = (start + k) % path.Count;
                var nxt2 = new Vector2((float)path[idx].x, (float)path[idx].y);
                var nxt  = P(nxt2);
                if (Vector3.DistanceSquared(cur, nxt) < 1e-6f) continue;
                layer.Moves.Add(new ToolpathMove(cur, nxt, MoveKind.Extrude)
                    { IsLightning = IsLightningSeg(cur2, nxt2) });
                cur = nxt; cur2 = nxt2;
            }
            if (Vector3.DistanceSquared(cur, first) > 1e-6f)
                layer.Moves.Add(new ToolpathMove(cur, first, MoveKind.Extrude)
                    { IsLightning = IsLightningSeg(cur2, first2) });

            int extrudeCount = layer.Moves.Count - extrudeStart;
            // Record contour so line-pick / seam tools can select whole loops
            // (Formbound Bridge/Buttress previously emitted moves without Contours).
            if (extrudeCount > 0)
                layer.Contours.Add(new ContourSpan(extrudeStart, extrudeCount, Closed: true, entryTravel));

            runningEnd = first;
            chainRef = first;
        }
    }

    private static int CountOuters(PathsD polys)
    {
        int outers = 0;
        foreach (var p in polys)
            if (Clipper.Area(p) > 0) outers++;
        return outers;
    }

    private static float DistToSegmentSq(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-12f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    private static float ArcLength(List<Vector2> line)
    {
        float len = 0f;
        for (int i = 1; i < line.Count; i++)
            len += Vector2.Distance(line[i - 1], line[i]);
        return len;
    }

    private static PathD Disc(Vector2 c, float r, int segments)
    {
        var path = new PathD(segments);
        for (int i = 0; i < segments; i++)
        {
            float a = i / (float)segments * 2f * MathF.PI;
            path.Add(new PointD(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a)));
        }
        return path;
    }
}
