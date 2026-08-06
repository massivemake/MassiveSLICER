using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// The single source of truth for extruder RPM demand, in motor-speed percent.
///
/// Every consumer -- the KRL exporter, the viewport highlight, the pre-export gate --
/// takes its number from here, so what the toolpath shows on screen is literally what
/// lands in the <c>.src</c>. Nothing downstream is allowed to adjust it: no post override
/// and no silent cap. A move demanding more than <see cref="MaxRpmPercent"/> blocks the
/// export outright rather than quietly extruding less than the path calls for.
/// </summary>
public static class ToolpathRpm
{
    /// <summary>
    /// Highest extruder motor speed (%) an exported program may ask for.
    /// The motor accepts 1-100 % in whole-percent steps, so a higher demand can only
    /// come out as a maxed 100 % -- the path would under-extrude for that stretch.
    /// </summary>
    public const float MaxRpmPercent = 99f;

    /// <summary>
    /// Per-move multiplier on the layer's nominal RPM: layer-adaptive speed scaling,
    /// post-travel resume ramps, wipe ramp-down, and Multi-Planar wedge thickness
    /// (flow follows the local layer thickness).
    /// </summary>
    public static float MoveScale(ToolpathMove move)
    {
        if (move.IsWipe)
            return move.WipeRpmScale;
        float scale = Math.Max(move.PrintSpeedScale, 1e-6f);
        if (move.IsResumeRamp)
            scale *= Math.Max(move.ResumeRpmScale, 1e-6f);
        scale *= Math.Max(move.HeightScale, 1e-6f);
        return scale;
    }

    /// <summary>Nominal RPM (%) for a layer, before per-move scaling.</summary>
    public static float BasePercent(KrlExportSettings s)
        => s.ExtrusionRpmPercent
           ?? KrlAnout.ComputeRpmPercent(s.BeadWidthMm, s.LayerHeightMm, s.PrintSpeedMps, s.FlowRate);

    /// <summary>
    /// Settings as they apply to one layer. Layer 0 may carry its own speed/RPM override;
    /// every other layer uses <paramref name="s"/> unchanged. The exporter and the gate both
    /// call this, so the checked number and the written number cannot drift apart.
    /// </summary>
    public static KrlExportSettings ForLayer(KrlExportSettings s, int layerIndex)
    {
        if (layerIndex != 0) return s;
        if (s.FirstLayerSpeedMps <= 1e-6f && s.FirstLayerRpmPercent <= 1e-6f) return s;
        return s with
        {
            PrintSpeedMps = s.FirstLayerSpeedMps > 1e-6f ? s.FirstLayerSpeedMps : s.PrintSpeedMps,
            ExtrusionRpmPercent = s.FirstLayerRpmPercent > 1e-6f
                ? s.FirstLayerRpmPercent : s.ExtrusionRpmPercent,
        };
    }

    /// <summary>Raw RPM demand (%) for one move under its layer's settings. Never capped.</summary>
    public static float MovePercent(ToolpathMove move, KrlExportSettings layerSettings)
        => BasePercent(layerSettings) * Math.Max(MoveScale(move), 0f);

    /// <summary>
    /// The whole-percent step the controller actually receives. Matches
    /// <see cref="KrlAnout.RoundAnout4UpToPercent"/>'s round-up, but is deliberately
    /// NOT capped at 100 -- an over-limit demand has to report its real size.
    /// </summary>
    public static float SteppedPercent(float rawPercent)
        => rawPercent <= 0f ? 0f : MathF.Ceiling(rawPercent - 1e-4f);

    /// <summary>True when this demand cannot be met by the extruder.</summary>
    public static bool IsOverLimit(float rawPercent) => SteppedPercent(rawPercent) > MaxRpmPercent;

    /// <summary>Moves that write an RPM value. Travel spins at idle; milling uses the spindle.</summary>
    public static bool WritesRpm(ToolpathMove move) => move.Kind == MoveKind.Extrude;

    /// <summary>A run of consecutive over-limit moves within one layer.</summary>
    public sealed record OverLimitSpan(
        int LayerIndex,
        float LayerZ,
        int FirstMoveIndex,
        int LastMoveIndex,
        float PeakPercent)
    {
        public int MoveCount => LastMoveIndex - FirstMoveIndex + 1;
    }

    /// <summary>
    /// Per-move RPM across a whole toolpath. <see cref="PerMovePercent"/> is indexed by
    /// flat move index (the same indexing the renderer, scrubber and reachability pass use);
    /// non-extrusion moves are <see cref="float.NaN"/>.
    /// </summary>
    public sealed record Analysis(
        float[] PerMovePercent,
        bool[] OverLimit,
        int OverCount,
        float PeakPercent,
        IReadOnlyList<OverLimitSpan> Spans)
    {
        public bool HasOverLimit => OverCount > 0;

        /// <summary>Empty result for "no toolpath yet" callers.</summary>
        public static Analysis Empty { get; } = new([], [], 0, 0f, []);
    }

    /// <summary>
    /// Computes the RPM every extrusion move will be exported with, and flags the ones the
    /// extruder cannot deliver. Cheap enough to run on every slice -- one multiply per move.
    /// </summary>
    public static Analysis Analyze(Toolpath toolpath, KrlExportSettings settings)
    {
        int total = 0;
        foreach (var layer in toolpath.Layers)
            total += layer.Moves.Count;
        if (total == 0) return Analysis.Empty;

        var perMove   = new float[total];
        var overLimit = new bool[total];
        var spans     = new List<OverLimitSpan>();

        int flat = 0, overCount = 0;
        float peak = 0f;

        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer  = toolpath.Layers[li];
            var layerS = ForLayer(settings, li);

            int spanFirst = -1, spanLast = -1;
            float spanPeak = 0f;

            for (int mi = 0; mi < layer.Moves.Count; mi++, flat++)
            {
                var move = layer.Moves[mi];
                if (!WritesRpm(move))
                {
                    perMove[flat] = float.NaN;
                    continue;
                }

                float raw = MovePercent(move, layerS);
                perMove[flat] = SteppedPercent(raw);
                if (raw > peak) peak = raw;

                if (!IsOverLimit(raw))
                {
                    if (spanFirst >= 0)
                    {
                        spans.Add(new OverLimitSpan(li, layer.Z, spanFirst, spanLast, spanPeak));
                        spanFirst = -1;
                        spanPeak  = 0f;
                    }
                    continue;
                }

                overLimit[flat] = true;
                overCount++;
                if (spanFirst < 0) { spanFirst = flat; spanPeak = 0f; }
                spanLast = flat;
                if (raw > spanPeak) spanPeak = raw;
            }

            // A span never crosses a layer boundary -- layers can carry different settings.
            if (spanFirst >= 0)
                spans.Add(new OverLimitSpan(li, layer.Z, spanFirst, spanLast, spanPeak));
        }

        return new Analysis(perMove, overLimit, overCount, peak, spans);
    }

    /// <summary>Longest-first list of the worst spans, for reports that show only a few.</summary>
    public static IEnumerable<OverLimitSpan> WorstFirst(Analysis a)
        => a.Spans.OrderByDescending(s => s.PeakPercent).ThenByDescending(s => s.MoveCount);

    /// <summary>One-line summary for the console, the status bar and the export dialog.</summary>
    public static string Describe(Analysis a)
    {
        if (!a.HasOverLimit)
            return $"RPM peaks at {a.PeakPercent:0.#} % — within the {MaxRpmPercent:0} % limit.";
        var first = a.Spans[0];
        return $"{a.OverCount:N0} move(s) over the {MaxRpmPercent:0} % RPM limit " +
               $"across {a.Spans.Count:N0} stretch(es), peaking at {a.PeakPercent:0.#} % " +
               $"(first at layer {first.LayerIndex}, Z {first.LayerZ:0.#} mm).";
    }
}

/// <summary>
/// Thrown instead of writing a program the extruder cannot run. Carries the analysis so
/// callers can point the operator at the offending stretch rather than just failing.
/// </summary>
public sealed class RpmLimitExceededException(ToolpathRpm.Analysis analysis)
    : InvalidOperationException(
        "Export blocked — " + ToolpathRpm.Describe(analysis) +
        " Lower the print speed, layer height or bead width until the peak is at or below " +
        $"{ToolpathRpm.MaxRpmPercent:0} %.")
{
    public ToolpathRpm.Analysis Analysis { get; } = analysis;
}
