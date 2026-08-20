using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// How far sideways each bead sits from the material under it — the single measurement
/// behind "is this bead going to be held up by the layer below".
///
/// For every extrusion move, the distance from that bead's midpoint to the nearest bead
/// <em>segment</em> in the layer below, in the XY plane. Point-to-segment, never
/// point-to-point: a layer carries only a few hundred vertices, so beads are ~12 mm
/// apart along the path and a nearest-<em>vertex</em> search measures polyline
/// discretisation instead of geometry. That mistake once read 18 % overlap where the
/// truth was 44 %; the tell is a figure that does not scale with layer height.
///
/// The offset is reported in <b>millimetres</b>, because that is a measured fact.
/// Turning it into an overlap percentage requires dividing by a bead width, and the
/// real bead width moves with layer height (measured 7.38 mm at 1 mm layers against
/// 5.3 mm at 3 mm layers), so the percentage carries an assumption the millimetres do
/// not. <see cref="Analysis.FractionAt"/> is provided for the viewport heatmap, which
/// needs a 0..1 value, and uses the nominal width it was handed.
///
/// This lives in Core rather than in the viewport so the slicer can read it: it is the
/// input for overhang-driven speed and for choosing layer heights, not only for drawing
/// a heatmap.
/// </summary>
public static class BeadSupport
{
    /// <summary>Per-layer summary, for reports that point at the worst places.</summary>
    public sealed record LayerStat(
        int LayerIndex,
        float Z,
        float MedianMm,
        float P99Mm,
        float MaxMm,
        float ExtrudedMm);

    /// <summary>
    /// Per-move support across a whole toolpath. <see cref="OffsetMm"/> is indexed by flat
    /// move index — the same indexing the renderer, the scrubber and
    /// <see cref="IO.ToolpathRpm"/> use, so the arrays line up move for move.
    /// </summary>
    public sealed record Analysis(
        float[] OffsetMm,
        float BeadWidthMm,
        int MeasuredMoves,
        float MedianMm,
        float P99Mm,
        float MaxMm,
        float TotalExtrudedMm,
        float ExtrudedMmUnderHalfOverlap,
        float ExtrudedMmUnderThreeQuarterOverlap,
        IReadOnlyList<LayerStat> Layers)
    {
        /// <summary>Empty result for "no toolpath yet" callers.</summary>
        public static Analysis Empty { get; } =
            new([], 0f, 0, 0f, 0f, 0f, 0f, 0f, 0f, []);

        /// <summary>
        /// Offset as a fraction of the nominal bead width, clamped to 0..1 — 0 is stacked
        /// squarely on the bead below, 1 is a full bead width off with nothing under it.
        /// Moves that were never measured (NaN) also read 0, which is what keeps the heatmap
        /// identical to before this moved into Core.
        /// </summary>
        public float FractionAt(int flatIndex)
        {
            if (BeadWidthMm <= 0f || flatIndex < 0 || flatIndex >= OffsetMm.Length) return 0f;
            float mm = OffsetMm[flatIndex];
            if (float.IsNaN(mm)) return 0f;
            if (float.IsPositiveInfinity(mm)) return 1f;
            return Math.Clamp(mm / BeadWidthMm, 0f, 1f);
        }

        /// <summary>How much of the bead sits over material, as a percentage of nominal width.</summary>
        public float OverlapPercentAt(int flatIndex) => 100f * (1f - FractionAt(flatIndex));
    }

    /// <summary>
    /// Measures every extrusion move against the layer below it.
    ///
    /// Three distinct results, and they must not be conflated:
    /// <list type="bullet">
    /// <item><b>NaN</b> — not measured. A travel move has no bead, and the first layer (or any
    /// layer whose predecessor laid nothing) has nothing beneath it to measure against.</item>
    /// <item><b>0</b> — measured, and stacked squarely on the bead below.</item>
    /// <item><b>PositiveInfinity</b> — measured, and nothing was found within roughly one bead
    /// width. The search cannot say how far the nearest material is, only that it is too far
    /// to matter.</item>
    /// </list>
    /// Treating NaN as a measured 0 would count phantom perfectly-supported beads and drag the
    /// median down; <see cref="Analysis.FractionAt"/> still maps it to 0 for the heatmap, which
    /// is what the pre-Core version did.
    ///
    /// Spatially hashed on a bead-width grid and scanned 3x3, which is exact for this
    /// purpose — a segment outside that neighbourhood is at least one bead width away, so it
    /// could not change any decision. One pass over the moves, not a per-layer pairwise
    /// search.
    /// </summary>
    public static float[] MeasureOffsets(
        Toolpath toolpath, float beadWidthMm, int maxRings = DefaultSearchRings)
    {
        int total = MoveCount(toolpath);
        if (total == 0 || beadWidthMm <= 0f) return [];

        var offsets = new float[total];
        Array.Fill(offsets, float.NaN);          // "not measured" until proven otherwise
        Measure(toolpath, beadWidthMm, offsets, maxRings);
        return offsets;
    }

    private static int MoveCount(Toolpath toolpath)
    {
        int total = 0;
        foreach (var layer in toolpath.Layers)
            total += layer.Moves.Count;
        return total;
    }

    /// <summary>
    /// The one measurement both entry points share, writing into <paramref name="dest"/> and
    /// leaving entries it did not measure untouched. Kept single so the render path and the
    /// report path cannot drift apart.
    /// </summary>
    private static void Measure(
        Toolpath toolpath, float beadWidthMm, float[] dest, int maxRings = DefaultSearchRings)
    {
        float cell = MathF.Max(beadWidthMm, 0.5f);

        Dictionary<(int, int), List<(Vector3 a, Vector3 b)>>? prevGrid = null;
        int flat = 0;

        foreach (var layer in toolpath.Layers)
        {
            var curGrid = new Dictionary<(int, int), List<(Vector3 a, Vector3 b)>>();
            foreach (var move in layer.Moves)
            {
                if (ToolpathMoveKinds.IsCutSegment(move.Kind))
                {
                    if (prevGrid is { Count: > 0 })
                    {
                        var mid = (move.From + move.To) * 0.5f;
                        dest[flat] = NearestSegmentDistance2D(mid, prevGrid, cell, maxRings);
                    }
                    InsertSegment(curGrid, move.From, move.To, cell);
                }
                flat++;
            }
            prevGrid = curGrid;
        }
    }

    /// <summary>
    /// The 0..1 array the viewport heatmap colours by, and nothing else.
    ///
    /// ⚠️ This is called from <c>UploadToolpathEntry</c>, which runs inside <c>OnRender</c> on
    /// the GL thread. It must do only the work the picture needs — no statistics, no sorting,
    /// no per-layer lists. Building those here added a ~292k-element sort to every toolpath
    /// upload on a real part, which lengthens the GL frame and widens the window on the
    /// workspace-restore scene-graph race. Use <see cref="Analyze"/> off the render path.
    /// </summary>
    public static float[] Fractions(Toolpath toolpath, float beadWidthMm)
    {
        int total = MoveCount(toolpath);
        var result = new float[total];
        if (total == 0 || beadWidthMm <= 0f) return result;

        // No NaN prefill and no second array: an unmeasured entry stays at its default 0,
        // which is the fraction it would map to anyway. One allocation, one measuring pass,
        // one in-place conversion — the same footprint the pre-Core version had.
        Measure(toolpath, beadWidthMm, result);
        for (int i = 0; i < result.Length; i++)
        {
            float mm = result[i];
            result[i] = float.IsPositiveInfinity(mm) ? 1f
                      : Math.Clamp(mm / beadWidthMm, 0f, 1f);
        }
        return result;
    }

    /// <summary>
    /// Full measurement plus the statistics a report needs. Sorts and allocates, so keep it
    /// off the render path — see <see cref="Fractions"/>.
    /// </summary>
    public static Analysis Analyze(Toolpath toolpath, float beadWidthMm)
    {
        var offsets = MeasureOffsets(toolpath, beadWidthMm);
        if (offsets.Length == 0 || beadWidthMm <= 0f) return Analysis.Empty;

        var layerStats  = new List<LayerStat>();
        var allMeasured = new List<float>();
        float totalExtruded = 0f, underHalf = 0f, underThreeQuarter = 0f;

        int flat = 0;
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer = toolpath.Layers[li];
            var layerMeasured = new List<float>();
            float layerExtruded = 0f;

            foreach (var move in layer.Moves)
            {
                // NaN means it was never measured — not a bead sitting perfectly supported.
                if (!float.IsNaN(offsets[flat]))
                {
                    float d   = offsets[flat];
                    float len = Vector3.Distance(move.From, move.To);

                    layerMeasured.Add(d);
                    allMeasured.Add(d);
                    layerExtruded += len;
                    totalExtruded += len;

                    float frac = float.IsPositiveInfinity(d)
                        ? 1f
                        : Math.Clamp(d / beadWidthMm, 0f, 1f);
                    if (frac > 0.50f) underHalf         += len;
                    if (frac > 0.25f) underThreeQuarter += len;
                }
                flat++;
            }

            if (layerMeasured.Count > 0)
            {
                layerMeasured.Sort();
                layerStats.Add(new LayerStat(
                    li, layer.Z,
                    Percentile(layerMeasured, 0.50f),
                    Percentile(layerMeasured, 0.99f),
                    layerMeasured[^1],
                    layerExtruded));
            }
        }

        allMeasured.Sort();
        return new Analysis(
            offsets,
            beadWidthMm,
            allMeasured.Count,
            Percentile(allMeasured, 0.50f),
            Percentile(allMeasured, 0.99f),
            allMeasured.Count > 0 ? allMeasured[^1] : 0f,
            totalExtruded,
            underHalf,
            underThreeQuarter,
            layerStats);
    }

    /// <summary>Worst places first, for a report that shows only a few lines.</summary>
    public static IEnumerable<LayerStat> WorstFirst(Analysis a)
        => a.Layers.OrderByDescending(l => l.MaxMm).ThenByDescending(l => l.P99Mm);

    /// <summary>
    /// One continuous stretch of an upper contour sitting further off the contour below than the
    /// target. <see cref="LengthMm"/> is measured along the path, which is what a bridging
    /// tolerance must gate on — a bead spans a gap or it does not, and that is a LOCAL property,
    /// so the tolerance is an absolute length rather than a share of the layer.
    /// </summary>
    public sealed record OffsetRun(float LengthMm, float WorstOffsetMm);

    /// <summary>
    /// Contour-to-contour measurement, for CHOOSING a layer height before any toolpath exists.
    ///
    /// Distinct from <see cref="Analyze"/>, which measures a finished toolpath. This works on the
    /// sliced boundary, which is the honest input for a thickness decision: the boundary is where
    /// the mesh is, whereas a toolpath has already had contour offsets, seams and modifiers
    /// applied. It is also the only version available at the point the decision must be made.
    ///
    /// Returns every stretch of <paramref name="upper"/> whose distance to the nearest segment of
    /// <paramref name="lower"/> exceeds <paramref name="targetOffsetMm"/>. Empty = all within target.
    /// </summary>
    public static List<OffsetRun> ContourOffsetRuns(
        IReadOnlyList<IReadOnlyList<Vector2>> upper,
        IReadOnlyList<IReadOnlyList<Vector2>> lower,
        float targetOffsetMm,
        float searchCellMm)
    {
        var runs = new List<OffsetRun>();
        if (upper.Count == 0 || lower.Count == 0 || targetOffsetMm <= 0f) return runs;

        float cell = MathF.Max(searchCellMm, 0.5f);
        var grid = new Dictionary<(int, int), List<(Vector2 a, Vector2 b)>>();
        foreach (var poly in lower)
            for (int i = 0; i + 1 < poly.Count; i++)
                Insert2D(grid, poly[i], poly[i + 1], cell);
        if (grid.Count == 0) return runs;

        foreach (var poly in upper)
        {
            float runLen = 0f, runWorst = 0f;
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                var  mid = (poly[i] + poly[i + 1]) * 0.5f;
                float d   = Nearest2D(mid, grid, cell);
                float seg = Vector2.Distance(poly[i], poly[i + 1]);

                if (d > targetOffsetMm)
                {
                    runLen += seg;
                    if (d > runWorst) runWorst = d;
                }
                else if (runLen > 0f)
                {
                    runs.Add(new OffsetRun(runLen, runWorst));
                    runLen = 0f; runWorst = 0f;
                }
            }
            if (runLen > 0f) runs.Add(new OffsetRun(runLen, runWorst));
        }
        return runs;
    }

    private static void Insert2D(
        Dictionary<(int, int), List<(Vector2 a, Vector2 b)>> grid, Vector2 a, Vector2 b, float cell)
    {
        int x0 = (int)MathF.Floor(MathF.Min(a.X, b.X) / cell), x1 = (int)MathF.Floor(MathF.Max(a.X, b.X) / cell);
        int y0 = (int)MathF.Floor(MathF.Min(a.Y, b.Y) / cell), y1 = (int)MathF.Floor(MathF.Max(a.Y, b.Y) / cell);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            if (!grid.TryGetValue((x, y), out var list)) grid[(x, y)] = list = [];
            list.Add((a, b));
        }
    }

    private static float Nearest2D(
        Vector2 p, Dictionary<(int, int), List<(Vector2 a, Vector2 b)>> grid, float cell)
    {
        int cx = (int)MathF.Floor(p.X / cell), cy = (int)MathF.Floor(p.Y / cell);
        float best = float.PositiveInfinity;
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
            if (grid.TryGetValue((gx, gy), out var segs))
                foreach (var (a, b) in segs)
                {
                    float d = SegmentDistance2D(new Vector3(p.X, p.Y, 0f),
                                                new Vector3(a.X, a.Y, 0f),
                                                new Vector3(b.X, b.Y, 0f));
                    if (d < best) best = d;
                }
        return best;
    }

    /// <summary>One-line summary for the console and the status bar.</summary>
    public static string Describe(Analysis a)
    {
        if (a.MeasuredMoves == 0) return "No bead support measured — need at least two layers.";
        float pctUnderHalf = a.TotalExtrudedMm > 0f
            ? 100f * a.ExtrudedMmUnderHalfOverlap / a.TotalExtrudedMm : 0f;
        return $"Sideways offset: median {a.MedianMm:0.##} mm, worst {Mm(a.MaxMm)} " +
               $"(bead {a.BeadWidthMm:0.##} mm). {pctUnderHalf:0.###} % of bead length is " +
               $"more than half a bead off.";
    }

    /// <summary>Formats an offset, spelling out the "further than we can tell" case.</summary>
    public static string Mm(float offsetMm)
        => float.IsPositiveInfinity(offsetMm) ? "unsupported" : $"{offsetMm:0.##} mm";

    // -- Support check (target-relative, for the overlay and its report) --------------------

    /// <summary>
    /// What the support rule says about one bead. Deliberately mirrors
    /// <c>SupportDrivenLayerHeights.Worst</c>: a stretch shorter than the bridge tolerance is one
    /// the slicer chose NOT to act on, so calling it a failure would show the operator problems
    /// the feature deliberately let go.
    /// </summary>
    public enum SupportVerdict : byte
    {
        /// <summary>Travel, or a layer with nothing beneath it. Never a pass and never a failure.</summary>
        NotMeasured = 0,

        /// <summary>Within the overlap target.</summary>
        OnTarget = 1,

        /// <summary>Past target, but its continuous run is short enough that the bead bridges it.</summary>
        Bridged = 2,

        /// <summary>Past target over a run longer than the bridge tolerance — a real miss.</summary>
        Failed = 3,
    }

    /// <summary>One continuous stretch of bead that failed, with where to find it.</summary>
    public sealed record SupportFailure(
        int LayerIndex,
        float Z,
        float LengthMm,
        float WorstOffsetMm,
        int FirstFlatIndex);

    /// <summary>
    /// Per-move verdicts for a finished toolpath, measured against the SAME target and tolerance
    /// the slicer used.
    /// </summary>
    public sealed record CheckResult(
        SupportVerdict[] Verdict,
        float[] OffsetMm,
        float TargetOffsetMm,
        float BridgeToleranceMm,
        float BeadWidthMm,
        float TotalExtrudedMm,
        float ExtrudedMmPastTarget,
        float ExtrudedMmFailed,
        IReadOnlyList<SupportFailure> Failures)
    {
        public static CheckResult Empty { get; } =
            new([], [], 0f, 0f, 0f, 0f, 0f, 0f, []);

        public bool HasFailures => Failures.Count > 0;

        /// <summary>Share of laid bead in a failing run, as a percentage of all laid bead.</summary>
        public float FailedPercent
            => TotalExtrudedMm > 1e-4f ? 100f * ExtrudedMmFailed / TotalExtrudedMm : 0f;

        /// <summary>Share of laid bead past target at all, failing or bridged.</summary>
        public float PastTargetPercent
            => TotalExtrudedMm > 1e-4f ? 100f * ExtrudedMmPastTarget / TotalExtrudedMm : 0f;
    }

    /// <summary>
    /// Classifies every bead against the overlap target, grouping into runs first so the
    /// bridge tolerance can be applied the way the slicer applies it.
    ///
    /// Measured with <see cref="ReportingSearchRings"/> rather than the 3x3 default, because this
    /// result is REPORTED in mm — a distance the overlay prints has to be a real one.
    /// </summary>
    /// <param name="targetOffsetMm">
    /// <c>SliceSettings.SupportTargetOffsetMm</c> — bead width x (1 - overlap target).
    /// </param>
    /// <param name="bridgeToleranceMm"><c>SliceSettings.ResolvedBridgeToleranceMm</c>.</param>
    public static CheckResult Check(
        Toolpath toolpath, float beadWidthMm, float targetOffsetMm, float bridgeToleranceMm)
    {
        int total = MoveCount(toolpath);
        if (total == 0 || beadWidthMm <= 0f || targetOffsetMm <= 0f) return CheckResult.Empty;

        var offsets  = MeasureOffsets(toolpath, beadWidthMm, ReportingSearchRings);
        var verdicts = new SupportVerdict[total];
        var failures = new List<SupportFailure>();

        // Accumulated in double, not float. A real part sums ~334k positive segment lengths to
        // ~3.5 m of bead, where one float ULP is already ~0.25 mm, and naive float summation of
        // same-sign terms drifts with the running total rather than cancelling: cross-checking
        // against an independent implementation on a 515-layer column showed the float version
        // over-reporting total bead by 4.7 m (0.13 %). The per-run lengths were unaffected —
        // they are short — but the totals feed the reported percentages.
        double totalMm = 0.0, pastMm = 0.0, failedMm = 0.0;

        // Reused across runs so a part with no overhang allocates nothing per layer.
        var runIdx = new List<int>();

        int flat = 0;
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer = toolpath.Layers[li];
            runIdx.Clear();
            float  runWorst = 0f;
            double runTotal = 0.0;

            // Closes the open run: everything in it becomes Bridged or Failed together, since
            // the verdict is a property of the RUN, not of the individual bead.
            void CloseRun()
            {
                if (runIdx.Count == 0) return;
                bool failed = runTotal > bridgeToleranceMm;
                var  v      = failed ? SupportVerdict.Failed : SupportVerdict.Bridged;
                for (int k = 0; k < runIdx.Count; k++) verdicts[runIdx[k]] = v;
                if (failed)
                {
                    failedMm += runTotal;
                    failures.Add(new SupportFailure(li, layer.Z, (float)runTotal, runWorst, runIdx[0]));
                }
                runIdx.Clear();
                runWorst = 0f; runTotal = 0.0;
            }

            for (int mi = 0; mi < layer.Moves.Count; mi++, flat++)
            {
                var move = layer.Moves[mi];
                if (!ToolpathMoveKinds.IsCutSegment(move.Kind))
                {
                    // The bead stops here, so any open run genuinely ends — a travel is not a
                    // continuation of the stretch before it.
                    CloseRun();
                    continue;
                }

                float len = Vector3.Distance(move.From, move.To);
                totalMm += len;

                float mm = offsets[flat];
                if (float.IsNaN(mm))
                {
                    // Measured nothing (first layer, or nothing laid below). Not a pass.
                    CloseRun();
                    continue;
                }

                if (mm > targetOffsetMm)
                {
                    pastMm += len;
                    runIdx.Add(flat);
                    runTotal += len;
                    if (mm > runWorst || float.IsPositiveInfinity(mm)) runWorst = mm;
                }
                else
                {
                    verdicts[flat] = SupportVerdict.OnTarget;
                    CloseRun();
                }
            }
            CloseRun();
        }

        failures.Sort((a, b) => b.LengthMm.CompareTo(a.LengthMm));
        return new CheckResult(
            verdicts, offsets, targetOffsetMm, bridgeToleranceMm, beadWidthMm,
            (float)totalMm, (float)pastMm, (float)failedMm, failures);
    }

    /// <summary>
    /// Band boundaries for the support-check colour ramp. Hard steps between classes with a
    /// slight gradient inside each, so a pass reads as a pass at a glance instead of asking the
    /// eye to tell 25 % from 45 % on a smooth ramp.
    /// </summary>
    public const float BandOnTargetMax = 0.30f;

    /// <inheritdoc cref="BandOnTargetMax"/>
    public const float BandBridgedMin = 0.40f;

    /// <inheritdoc cref="BandOnTargetMax"/>
    public const float BandBridgedMax = 0.60f;

    /// <inheritdoc cref="BandOnTargetMax"/>
    public const float BandFailedMin = 0.70f;

    /// <summary>
    /// Packs a verdict plus its severity into the 0..1 score the renderer colours by, so the
    /// existing single-float bead VAO can carry a banded map with no vertex-format change.
    /// </summary>
    public static float[] CheckScores(CheckResult r)
    {
        var scores = new float[r.Verdict.Length];
        if (r.TargetOffsetMm <= 0f) return scores;

        // Severity runs from target out to a full bead past target; beyond that it is pinned.
        float span = MathF.Max(r.BeadWidthMm, r.TargetOffsetMm);

        for (int i = 0; i < scores.Length; i++)
        {
            float mm = r.OffsetMm.Length > i ? r.OffsetMm[i] : float.NaN;
            switch (r.Verdict[i])
            {
                case SupportVerdict.OnTarget:
                    // How much of the allowance is used up — 0 stacked square, 0.30 right at target.
                    float used = float.IsNaN(mm) ? 0f : Math.Clamp(mm / r.TargetOffsetMm, 0f, 1f);
                    scores[i] = used * BandOnTargetMax;
                    break;

                case SupportVerdict.Bridged:
                    scores[i] = BandBridgedMin
                              + Severity(mm, r.TargetOffsetMm, span) * (BandBridgedMax - BandBridgedMin);
                    break;

                case SupportVerdict.Failed:
                    scores[i] = BandFailedMin
                              + Severity(mm, r.TargetOffsetMm, span) * (1f - BandFailedMin);
                    break;

                default:
                    scores[i] = 0f;   // NotMeasured reads as the safe end, as the old heatmap did
                    break;
            }
        }
        return scores;
    }

    private static float Severity(float mm, float targetMm, float spanMm)
    {
        if (float.IsPositiveInfinity(mm)) return 1f;
        if (float.IsNaN(mm)) return 0f;
        return Math.Clamp((mm - targetMm) / MathF.Max(spanMm, 1e-4f), 0f, 1f);
    }

    /// <summary>One-line summary for the status bar and the console.</summary>
    public static string Describe(CheckResult r)
    {
        if (r.TotalExtrudedMm <= 1e-4f) return "No bead measured — need at least two layers.";
        if (!r.HasFailures)
            return $"All bead within {r.TargetOffsetMm:0.##} mm of target "
                 + $"({r.PastTargetPercent:0.###} % past target, all bridged).";
        return $"{r.Failures.Count} stretch(es) past target over more than "
             + $"{r.BridgeToleranceMm:0.##} mm — {r.ExtrudedMmFailed / 1000f:0.##} m "
             + $"({r.FailedPercent:0.###} % of bead). Worst {Mm(r.Failures[0].WorstOffsetMm)} "
             + $"over {r.Failures[0].LengthMm:0.#} mm at Z {r.Failures[0].Z:0.#}.";
    }

    /// <summary>
    /// Rings of grid cells to scan outward from the point's own cell. 1 = the classic 3x3
    /// neighbourhood, which can only report "nearer than one cell" or "infinity".
    ///
    /// ⚠️ Anything that divides BY the measured distance needs more than one ring. A 3x3 scan
    /// returns <see cref="float.PositiveInfinity"/> for a bead further off than one cell, and
    /// <c>SupportDrivenLayerHeights.Refine</c> then computes <c>needed = h * target / inf = 0</c>
    /// and prints "would need h 0 mm" — a search-window artifact that reads as floating geometry.
    /// One such layer measured 14.17 mm by brute force over 0.24 % of the contour.
    /// </summary>
    public const int DefaultSearchRings = 1;

    /// <inheritdoc cref="DefaultSearchRings"/>
    /// <remarks>
    /// Used by anything that REPORTS a distance rather than just comparing it to one cell:
    /// the support-check overlay and its console report. At a 6 mm bead this reaches 24 mm,
    /// past every real overhang measured on a part so far. The ring walk early-exits as soon as
    /// the best hit cannot be beaten from further out, so on well-supported bead — the common
    /// case — it costs the same as the 3x3 scan.
    /// </remarks>
    public const int ReportingSearchRings = 4;

    private static float NearestSegmentDistance2D(
        Vector3 p,
        Dictionary<(int, int), List<(Vector3 a, Vector3 b)>> grid,
        float cell,
        int maxRings = DefaultSearchRings)
    {
        int cx = (int)MathF.Floor(p.X / cell);
        int cy = (int)MathF.Floor(p.Y / cell);
        float best = float.PositiveInfinity;

        for (int ring = 0; ring <= maxRings; ring++)
        {
            ScanRing(p, grid, cx, cy, ring, ref best);

            // Nothing outside this ring can be nearer than the ring's own inner edge, so once
            // the best hit is inside that edge the answer cannot improve. Checked AFTER the
            // ring is scanned, so ring 0 alone never short-circuits a nearer ring-1 segment.
            if (best <= ring * cell) break;
        }
        return best;
    }

    private static void ScanRing(
        Vector3 p,
        Dictionary<(int, int), List<(Vector3 a, Vector3 b)>> grid,
        int cx, int cy, int ring, ref float best)
    {
        for (int gx = cx - ring; gx <= cx + ring; gx++)
        for (int gy = cy - ring; gy <= cy + ring; gy++)
        {
            // Only the perimeter of this ring is new — the interior was scanned already.
            if (ring > 0 && Math.Abs(gx - cx) != ring && Math.Abs(gy - cy) != ring) continue;
            if (!grid.TryGetValue((gx, gy), out var segs)) continue;
            foreach (var (a, b) in segs)
            {
                float d = SegmentDistance2D(p, a, b);
                if (d < best) best = d;
            }
        }
    }

    private static void InsertSegment(
        Dictionary<(int, int), List<(Vector3 a, Vector3 b)>> grid,
        Vector3 a, Vector3 b, float cell)
    {
        int x0 = (int)MathF.Floor(MathF.Min(a.X, b.X) / cell);
        int x1 = (int)MathF.Floor(MathF.Max(a.X, b.X) / cell);
        int y0 = (int)MathF.Floor(MathF.Min(a.Y, b.Y) / cell);
        int y1 = (int)MathF.Floor(MathF.Max(a.Y, b.Y) / cell);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            if (!grid.TryGetValue((x, y), out var list))
                grid[(x, y)] = list = [];
            list.Add((a, b));
        }
    }

    /// <summary>Distance from a point to a segment, in XY only — Z is the layer step, not offset.</summary>
    private static float SegmentDistance2D(Vector3 p, Vector3 a, Vector3 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-10f)
        {
            float ex = p.X - a.X, ey = p.Y - a.Y;
            return MathF.Sqrt(ex * ex + ey * ey);
        }
        float t  = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0f, 1f);
        float cx = a.X + t * dx - p.X, cy = a.Y + t * dy - p.Y;
        return MathF.Sqrt(cx * cx + cy * cy);
    }

    /// <summary>Percentile of an already-sorted list. Infinities sort last, so they win the top end.</summary>
    private static float Percentile(List<float> sorted, float q)
    {
        if (sorted.Count == 0) return 0f;
        int i = (int)(q * (sorted.Count - 1));
        return sorted[Math.Clamp(i, 0, sorted.Count - 1)];
    }
}
