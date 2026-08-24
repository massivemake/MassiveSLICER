using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>
/// Diagnostic census against the REAL Swan_Column_Top_02 — the part every proximity number in the
/// notes was measured on. Skips silently when the model isn't on this machine.
///
/// <para><b>The question.</b> The structure hold currently carries reduced flow across bead that
/// <see cref="BeadProximity.MeasureGaps"/> never measured as crowded. An earlier hand analysis said
/// two thirds of that held bead really IS within a bead width of a neighbour and is only missed
/// because the sleeve is ANGLED and fails the <see cref="BeadProximity.ParallelDot"/> test. But that
/// analysis excluded neighbours by SHARED ENDPOINT, which is weaker than the cyclic arc-distance
/// filter MeasureGaps uses — on a curved wall chopped into ~3.5 mm chords, move i and move i+3 share
/// no endpoint yet sit ~10 mm along the same wall with almost no perpendicular distance. That is the
/// seam artifact wearing a different hat, and it would inflate the "genuinely crowded" share.</para>
///
/// <para><b>The instrument.</b> Each bead's nearest neighbour is searched TWICE over the same grid,
/// same arc exclusion: once with the direction filter (which must reproduce MeasureGaps exactly, and
/// is asserted to) and once without it. Moves where the two disagree are exactly the population the
/// |cos| test discards. Reporting their gap and |cos| distributions says whether relaxing the
/// threshold is justified, and to what value.</para>
/// </summary>
// Slicing publishes the shared diagnostic statics (AdaptiveLayerHeights.LastReasons,
// SupportDrivenLayerHeights.LastDecisions, ProximityFlowPostProcessor.LastRuns) as a side
// effect, even when this test never reads them. xUnit runs test CLASSES in parallel, so a
// class that slices outside this collection clobbers whatever LayerLadderAgreementTest is
// asserting on. Any test that runs the slicer belongs in this collection.
[Collection("AdaptiveLayerHeights")]
public class ProximityDirectionCensusCheck(ITestOutputHelper o)
{
    private const string Stl = @"D:\MASSIVE\JEFRE\3D Prints\Swan\Swan_Column_Top_02.stl";

    private const float Bead = 8f;

    /// <summary>
    /// The validated configuration: bead 8 / layer 4 / floor 2 / adaptive + support-driven +
    /// slew 0.2, proximity on, 92 mm/s.
    ///
    /// <para>⚠️ <b><see cref="SliceSettings.DisableContourOffset"/> must be ON</b>, and no note
    /// recorded that. On these parts the MODEL IS THE CENTRELINE — the arms are drawn at the 6 mm
    /// pitch the toolpath is meant to run. With the half-bead inset enabled the arm walls are pushed
    /// APART rather than together, because the arm is a slot between two solids and each wall insets
    /// into its own side: measured pitch came out bead + 6.5 mm (12.5 / 14.5 / 16.5 mm at beads
    /// 6 / 8 / 10), so the 6 mm crowding vanished entirely and the correction found nothing. With it
    /// off this reproduces the recorded run to within half a percent — 391 layers / 2703.1 m /
    /// 913.1 m of arm at a 6 mm pitch, against the noted 392 / 2709 / 917.0.</para>
    /// </summary>
    private static SliceSettings Validated(float bead = Bead, bool noOffset = true) => new()
    {
        BeadWidth                      = bead,
        DisableContourOffset           = noOffset,
        LayerHeight                    = 4f,
        FirstLayerHeight               = 4f,
        MinLayerHeight                 = 2f,
        AdaptiveLayerHeight            = true,
        SupportDrivenLayerHeight       = true,
        MaxLayerHeightChangeMm         = 0.2f,
        PrintSpeedMps                  = 0.092f,
        ProximityCorrectionEnabled     = true,
        ProximityHoldThroughStructure  = true,
        MaxFlowChangePercentPerSecond  = 2f,
    };

    [Fact]
    public void Census_of_what_the_direction_filter_discards()
    {
        if (!File.Exists(Stl)) { o.WriteLine($"SKIP — model not on this machine: {Stl}"); return; }

        var tris = LoadStlFlipped180X(Stl);
        o.WriteLine($"mesh: {tris.Length / 3:N0} triangles");
        Bounds(tris, out var lo, out var hi);
        o.WriteLine($"bounds after flip+drop: X {lo.X:0.#}..{hi.X:0.#}  Y {lo.Y:0.#}..{hi.Y:0.#}  Z {lo.Z:0.#}..{hi.Z:0.#}");

        var tp = PlanarSlicer.Slice([tris], Validated());
        o.WriteLine($"sliced: {tp.Layers.Count} layers, {tp.Layers.Sum(l => l.Moves.Count):N0} moves");
        if (tp.Layers.Count == 0) { o.WriteLine("no layers — nothing to census"); return; }

        // ---- the instrument -------------------------------------------------------------
        var withFilter    = Census(tp, Bead, applyDirectionFilter: true);
        var withoutFilter = Census(tp, Bead, applyDirectionFilter: false);

        // ---- CONTROL: the filtered pass must BE MeasureGaps, or the instrument is lying ---
        var truth = BeadProximity.MeasureGaps(tp, Bead);
        int mismatches = 0;
        for (int i = 0; i < truth.Length; i++)
        {
            float mine = withFilter[i].Gap < Bead ? withFilter[i].Gap : float.NaN;
            bool bothNaN = float.IsNaN(truth[i]) && float.IsNaN(mine);
            if (!bothNaN && MathF.Abs(truth[i] - mine) > 1e-3f) mismatches++;
        }
        o.WriteLine($"CONTROL — census-with-filter vs MeasureGaps: {mismatches} mismatches "
                  + $"(must be 0, else every number below is suspect)");
        Assert.Equal(0, mismatches);

        // ---- the population the direction filter discards ---------------------------------
        var moves = tp.Layers.SelectMany(l => l.Moves).ToArray();

        double totalExtrude = 0, measuredLen = 0, discardedLen = 0, reducedLen = 0, heldLen = 0;
        var discardGapHist = new SortedDictionary<int, double>();   // gap mm bucket -> length
        var discardCosHist = new SortedDictionary<int, double>();   // |cos|*10 bucket -> length
        var heldTrueGap    = new SortedDictionary<string, double>();

        for (int i = 0; i < moves.Length; i++)
        {
            var m = moves[i];
            if (!ToolpathMoveKinds.IsCutSegment(m.Kind) || m.IsBrim) continue;
            float len = Vector3.Distance(m.From, m.To);
            if (len < 1e-6f) continue;
            totalExtrude += len;

            bool measured = !float.IsNaN(truth[i]);
            bool reduced  = m.WidthScale < 1f - 1e-5f;
            if (measured) measuredLen += len;
            if (reduced)  reducedLen  += len;

            // Held / collateral: flow was pulled down on bead we never measured as crowded.
            if (reduced && !measured)
            {
                heldLen += len;
                var u = withoutFilter[i];
                string bucket = float.IsNaN(u.Gap) || u.Gap >= Bead ? ">= bead (truly isolated)"
                              : u.Gap < Bead                        ? "< bead (a real neighbour is there)"
                              : "?";
                heldTrueGap.TryGetValue(bucket, out double had);
                heldTrueGap[bucket] = had + len;
            }

            // Discarded purely on ANGLE: a neighbour inside a bead width that the filter refused.
            if (!measured && !float.IsNaN(withoutFilter[i].Gap) && withoutFilter[i].Gap < Bead)
            {
                discardedLen += len;
                int gb = (int)MathF.Floor(withoutFilter[i].Gap);
                discardGapHist.TryGetValue(gb, out double g0);
                discardGapHist[gb] = g0 + len;

                int cb = (int)MathF.Floor(withoutFilter[i].Cos * 10f);
                discardCosHist.TryGetValue(cb, out double c0);
                discardCosHist[cb] = c0 + len;
            }
        }

        o.WriteLine("");
        o.WriteLine($"total extruded bead      {totalExtrude / 1000.0,10:0.0} m");
        o.WriteLine($"  measured as crowded    {measuredLen  / 1000.0,10:0.0} m  ({measuredLen / totalExtrude:P1})");
        o.WriteLine($"  flow reduced           {reducedLen   / 1000.0,10:0.0} m  ({reducedLen  / totalExtrude:P1})");
        o.WriteLine($"  reduced but NOT measured (held + ramp collateral)");
        o.WriteLine($"                         {heldLen      / 1000.0,10:0.0} m  ({heldLen     / totalExtrude:P1})");
        o.WriteLine("");
        o.WriteLine("Of that reduced-but-not-measured bead, where is its nearest neighbour REALLY");
        o.WriteLine("(same arc exclusion, direction filter OFF):");
        foreach (var (k, v) in heldTrueGap.OrderByDescending(kv => kv.Value))
            o.WriteLine($"    {k,-40} {v / 1000.0,8:0.000} m  ({v / Math.Max(heldLen, 1e-9):P1})");

        // ---- where does the nearest neighbour actually sit, across the WHOLE part? ---------
        // If the 6 mm arm population is absent here it is not the direction filter's doing, and
        // nothing below it can be trusted as an answer about the sleeve.
        o.WriteLine("");
        o.WriteLine("Nearest-neighbour gap over ALL bead (arc-excluded, direction filter OFF),");
        o.WriteLine("searched out to 4 bead widths:");
        var wide = CensusWide(tp, Bead, Bead * 4f);
        var wideHist = new SortedDictionary<int, double>();
        double unreached = 0;
        for (int i = 0; i < moves.Length; i++)
        {
            var m = moves[i];
            if (!ToolpathMoveKinds.IsCutSegment(m.Kind) || m.IsBrim) continue;
            float len = Vector3.Distance(m.From, m.To);
            if (len < 1e-6f) continue;
            if (float.IsNaN(wide[i].Gap)) { unreached += len; continue; }
            int b = (int)MathF.Floor(wide[i].Gap);
            wideHist.TryGetValue(b, out double h0);
            wideHist[b] = h0 + len;
        }
        foreach (var (k, v) in wideHist)
            if (v > 1.0)
                o.WriteLine($"    {k,3}-{k + 1,-3} mm  {v / 1000.0,9:0.000} m");
        o.WriteLine($"    nothing within {Bead * 4f:0.#} mm: {unreached / 1000.0:0.000} m");

        o.WriteLine("");
        o.WriteLine("Per-layer shape (every 60th layer):");
        int lb = 0;
        for (int li = 0; li < tp.Layers.Count; li++)
        {
            var lyr = tp.Layers[li];
            if (li % 60 == 0)
            {
                int ext = lyr.Moves.Count(mm => mm.Kind == MoveKind.Extrude);
                int trv = lyr.Moves.Count(mm => mm.Kind == MoveKind.Travel);
                double mlen = lyr.Moves.Where(mm => mm.Kind == MoveKind.Extrude)
                                       .Sum(mm => (double)Vector3.Distance(mm.From, mm.To));
                int crowd = 0;
                for (int k = 0; k < lyr.Moves.Count; k++) if (!float.IsNaN(truth[lb + k])) crowd++;
                o.WriteLine($"    L{li,3} Z{lyr.Z,8:0.0}  extrude {ext,5}  travel {trv,3}  "
                          + $"{mlen / 1000.0,7:0.000} m  crowded moves {crowd}");
            }
            lb += lyr.Moves.Count;
        }

        o.WriteLine("");
        o.WriteLine($"BEAD DISCARDED ON ANGLE ALONE: {discardedLen / 1000.0:0.000} m "
                  + $"({discardedLen / totalExtrude:P2} of the print)");
        o.WriteLine("  by gap to that neighbour:");
        foreach (var (k, v) in discardGapHist)
            o.WriteLine($"    {k}-{k + 1} mm      {v / 1000.0,8:0.000} m");
        o.WriteLine($"  by |cos| (filter rejects below {BeadProximity.ParallelDot:0.0}):");
        foreach (var (k, v) in discardCosHist)
            o.WriteLine($"    {k / 10.0:0.0}-{(k + 1) / 10.0:0.0}    {v / 1000.0,8:0.000} m");
    }

    // ------------------------------------------------------------------------------------
    private readonly record struct Hit(float Gap, float Cos);

    /// <summary>
    /// A copy of <see cref="BeadProximity.MeasureGaps"/>'s search that also reports the |cos| of the
    /// winning candidate and can run with the direction filter disabled. Deliberately a copy: the
    /// filtered pass is asserted equal to MeasureGaps, so any drift between the two shows up as a
    /// failing control rather than as a quietly wrong census.
    /// </summary>
    /// <summary>Same search, no direction filter, reporting out to <paramref name="reachMm"/> rather
    /// than stopping at a bead width — so a population sitting just outside the bead is still seen.
    /// The arc exclusion stays tied to the bead so the two censuses stay comparable.</summary>
    private static Hit[] CensusWide(Toolpath toolpath, float beadWidthMm, float reachMm) =>
        Census(toolpath, beadWidthMm, applyDirectionFilter: false, reachMm: reachMm);

    private static Hit[] Census(
        Toolpath toolpath, float beadWidthMm, bool applyDirectionFilter, float reachMm = -1f)
    {
        int total = toolpath.Layers.Sum(l => l.Moves.Count);
        var outp  = new Hit[total];
        Array.Fill(outp, new Hit(float.NaN, float.NaN));
        if (total == 0 || beadWidthMm <= 0f) return outp;

        float reach   = reachMm > 0f ? reachMm : beadWidthMm;
        float cell    = MathF.Max(reach, 0.5f);
        float arcSkip = BeadProximity.PathSkipBeads * beadWidthMm;

        int flat = 0;
        foreach (var layer in toolpath.Layers)
        {
            var moves = layer.Moves;
            int n = moves.Count;

            var arc = new float[n + 1];
            for (int i = 0; i < n; i++)
                arc[i + 1] = arc[i] + Vector3.Distance(moves[i].From, moves[i].To);
            float totalArc = arc[n];

            var grid = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < n; i++)
            {
                if (!ToolpathMoveKinds.IsCutSegment(moves[i].Kind)) continue;
                Insert(grid, moves[i].From, moves[i].To, cell, i);
            }

            for (int i = 0; i < n; i++, flat++)
            {
                var move = moves[i];
                if (!ToolpathMoveKinds.IsCutSegment(move.Kind)) continue;

                var d = move.To - move.From;
                float len = new Vector2(d.X, d.Y).Length();
                if (len < 1e-6f) continue;
                float ux = d.X / len, uy = d.Y / len;

                float mx = (move.From.X + move.To.X) * 0.5f;
                float my = (move.From.Y + move.To.Y) * 0.5f;

                int cx = (int)MathF.Floor(mx / cell), cy = (int)MathF.Floor(my / cell);
                float best = float.PositiveInfinity, bestCos = float.NaN;

                for (int gx = cx - 1; gx <= cx + 1; gx++)
                for (int gy = cy - 1; gy <= cy + 1; gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var bucket)) continue;
                    foreach (int j in bucket)
                    {
                        if (j == i) continue;

                        float da = MathF.Abs(arc[j] - arc[i]);
                        da = MathF.Min(da, totalArc - da);
                        if (da <= arcSkip) continue;

                        var o2 = moves[j];
                        var od = o2.To - o2.From;
                        float ol = new Vector2(od.X, od.Y).Length();
                        if (ol < 1e-6f) continue;

                        float cos = MathF.Abs(ux * od.X / ol + uy * od.Y / ol);
                        if (applyDirectionFilter && cos < BeadProximity.ParallelDot) continue;

                        float dist = SegmentDistance2D(mx, my, o2.From, o2.To);
                        if (dist < best) { best = dist; bestCos = cos; }
                    }
                }

                if (best < reach) outp[flat] = new Hit(best, bestCos);
            }
        }
        return outp;
    }

    private static void Insert(
        Dictionary<(int, int), List<int>> grid, Vector3 a, Vector3 b, float cell, int index)
    {
        int x0 = (int)MathF.Floor(MathF.Min(a.X, b.X) / cell), x1 = (int)MathF.Floor(MathF.Max(a.X, b.X) / cell);
        int y0 = (int)MathF.Floor(MathF.Min(a.Y, b.Y) / cell), y1 = (int)MathF.Floor(MathF.Max(a.Y, b.Y) / cell);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            if (!grid.TryGetValue((x, y), out var list)) grid[(x, y)] = list = [];
            list.Add(index);
        }
    }

    private static float SegmentDistance2D(float px, float py, Vector3 a, Vector3 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float l2 = dx * dx + dy * dy;
        if (l2 < 1e-10f) return MathF.Sqrt((px - a.X) * (px - a.X) + (py - a.Y) * (py - a.Y));
        float t  = Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / l2, 0f, 1f);
        float ex = a.X + t * dx - px, ey = a.Y + t * dy - py;
        return MathF.Sqrt(ex * ex + ey * ey);
    }

    /// <summary>Binary STL, flipped 180 degrees about X the way the validation run had it, then
    /// dropped so the lowest point sits at Z 0.</summary>
    private static Vector3[] LoadStlFlipped180X(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        br.ReadBytes(80);
        uint n = br.ReadUInt32();
        var tris = new Vector3[n * 3];
        for (int i = 0; i < n; i++)
        {
            br.ReadBytes(12);                       // face normal, recomputed downstream
            for (int v = 0; v < 3; v++)
                tris[i * 3 + v] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            br.ReadBytes(2);                        // attribute byte count
        }

        float minZ = float.MaxValue;
        for (int i = 0; i < tris.Length; i++)
        {
            tris[i] = new Vector3(tris[i].X, -tris[i].Y, -tris[i].Z);   // 180 about X
            minZ = MathF.Min(minZ, tris[i].Z);
        }
        for (int i = 0; i < tris.Length; i++) tris[i].Z -= minZ;
        return tris;
    }

    private static void Bounds(Vector3[] v, out Vector3 lo, out Vector3 hi)
    {
        lo = new Vector3(float.MaxValue); hi = new Vector3(float.MinValue);
        foreach (var p in v) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
    }

    /// <summary>Discriminator: if the half-bead inset is being applied, the arm-wall PITCH must move
    /// with the bead width (pitch = solid width - bead). If the peak bucket sits at the same place
    /// for every bead, the contours are raw cross-sections and the inset is being skipped.</summary>
    [Fact]
    public void Does_the_arm_pitch_move_with_the_bead_width()
    {
        if (!File.Exists(Stl)) { o.WriteLine($"SKIP — model not on this machine: {Stl}"); return; }
        var tris = LoadStlFlipped180X(Stl);

        foreach (var (bead, noOffset) in new[]
                 { (6f, false), (8f, false), (10f, false), (8f, true) })
        {
            var tp = PlanarSlicer.Slice([tris], Validated(bead, noOffset));
            var wide = CensusWide(tp, bead, 40f);
            var moves = tp.Layers.SelectMany(l => l.Moves).ToArray();

            var hist = new SortedDictionary<int, double>();
            for (int i = 0; i < moves.Length; i++)
            {
                var m = moves[i];
                if (!ToolpathMoveKinds.IsCutSegment(m.Kind) || m.IsBrim) continue;
                float len = Vector3.Distance(m.From, m.To);
                if (len < 1e-6f || float.IsNaN(wide[i].Gap)) continue;
                int b = (int)MathF.Floor(wide[i].Gap);
                hist.TryGetValue(b, out double h0);
                hist[b] = h0 + len;
            }
            var peak = hist.OrderByDescending(kv => kv.Value).First();
            double total = moves.Where(m => ToolpathMoveKinds.IsCutSegment(m.Kind))
                                .Sum(m => (double)Vector3.Distance(m.From, m.To));
            o.WriteLine($"bead {bead,4:0.#}  DisableContourOffset={noOffset,-5}  "
                      + $"{tp.Layers.Count,3} layers  {total / 1000.0,7:0.0} m  "
                      + $"peak gap bucket {peak.Key}-{peak.Key + 1} mm holding {peak.Value / 1000.0:0.0} m");
        }
    }
}
