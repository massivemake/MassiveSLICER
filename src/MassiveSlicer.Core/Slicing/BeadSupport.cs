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
    public static float[] MeasureOffsets(Toolpath toolpath, float beadWidthMm)
    {
        int total = 0;
        foreach (var layer in toolpath.Layers)
            total += layer.Moves.Count;
        if (total == 0 || beadWidthMm <= 0f) return [];

        var offsets = new float[total];
        Array.Fill(offsets, float.NaN);          // "not measured" until proven otherwise
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
                        offsets[flat] = NearestSegmentDistance2D(mid, prevGrid, cell);
                    }
                    InsertSegment(curGrid, move.From, move.To, cell);
                }
                flat++;
            }
            prevGrid = curGrid;
        }

        return offsets;
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
        var offsets = MeasureOffsets(toolpath, beadWidthMm);
        var result  = new float[offsets.Length];
        if (beadWidthMm <= 0f) return result;
        for (int i = 0; i < offsets.Length; i++)
        {
            float mm = offsets[i];
            result[i] = float.IsNaN(mm) ? 0f
                      : float.IsPositiveInfinity(mm) ? 1f
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

    private static float NearestSegmentDistance2D(
        Vector3 p,
        Dictionary<(int, int), List<(Vector3 a, Vector3 b)>> grid,
        float cell)
    {
        int cx = (int)MathF.Floor(p.X / cell);
        int cy = (int)MathF.Floor(p.Y / cell);
        float best = float.PositiveInfinity;
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
            if (grid.TryGetValue((gx, gy), out var segs))
                foreach (var (a, b) in segs)
                {
                    float d = SegmentDistance2D(p, a, b);
                    if (d < best) best = d;
                }
        return best;
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
