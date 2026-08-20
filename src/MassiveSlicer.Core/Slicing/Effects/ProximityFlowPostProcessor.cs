using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Reduces extrusion flow where two beads on the SAME layer run alongside each other closer than a
/// bead width, so the strip they share is not deposited twice.
///
/// The horizontal counterpart of <see cref="LayerHeightFlowPostProcessor"/>: that one makes flow
/// follow the real layer THICKNESS, this one makes it follow the real bead TERRITORY. It rides on
/// <see cref="ToolpathMove.WidthScale"/>, so every downstream consumer picks it up unchanged —
/// <see cref="IO.ToolpathRpm.MoveScale"/>, the KRL exporter, the RPM view and gradient, and the
/// 99 % export gate.
///
/// <para><b>Only long runs are corrected.</b> A bead alongside another for a few millimetres — the
/// outer wall clipping past the end of an internal arm — is not worth acting on: at 85 mm/s a 12 mm
/// stretch is 0.14 s, far inside the extruder's transport lag, so the RPM change could not land
/// there and would only put the wrong flow somewhere else. Measured on a real part the two
/// populations separate with nothing at all in between: 2237 runs under 15 mm (7.7 % of crowded
/// bead) against 784 runs of 360-366 mm (91.2 %), and an empty band from 60 to 250 mm. Any
/// threshold inside that band gives identical results.</para>
///
/// <para>Scales are always &lt;= 1, so this can only reduce commanded flow — it cannot push a
/// previously-valid job over the export limit. Idempotent: the scale is assigned, never
/// accumulated.</para>
/// </summary>
public static class ProximityFlowPostProcessor
{
    /// <summary>One continuous stretch of bead running alongside a neighbour.</summary>
    public sealed record Run(
        int LayerIndex,
        float Z,
        float LengthMm,
        float ClosestGapMm,
        float MeanScale,
        bool Corrected,
        int FirstFlatIndex);

    /// <summary>What the last <see cref="Apply"/> did. Diagnostics; nothing reads it to decide.</summary>
    public static IReadOnlyList<Run> LastRuns => s_last;
    private static Run[] s_last = [];

    /// <summary>Drops published runs, so a report cannot describe a slice that is no longer live.</summary>
    public static void ResetRuns() => s_last = [];

    /// <summary>
    /// Stamps <see cref="ToolpathMove.WidthScale"/> on crowded bead whose run is long enough to act
    /// on. No-op unless <see cref="SliceSettings.ProximityCorrectionEnabled"/>.
    /// </summary>
    public static void Apply(Toolpath toolpath, SliceSettings settings)
    {
        if (!settings.ProximityCorrectionEnabled) { ResetRuns(); return; }

        float bead = settings.BeadWidth;
        if (bead <= 0f || toolpath.Layers.Count == 0) { ResetRuns(); return; }

        var gaps   = BeadProximity.MeasureGaps(toolpath, bead);
        float minRun = MathF.Max(settings.ProximityMinRunLengthMm, 0f);

        var runs = FindRuns(toolpath, gaps, bead, minRun);

        // Applied only after every run's verdict is settled, so a move's fate is decided by the
        // length of the whole stretch it belongs to rather than by the move itself.
        var qualifying = new HashSet<int>();
        foreach (var r in runs)
            if (r.Corrected)
                for (int i = 0; i < r.MoveCount; i++) qualifying.Add(r.FirstFlatIndex + i);

        int flat = 0;
        foreach (var layer in toolpath.Layers)
        {
            for (int mi = 0; mi < layer.Moves.Count; mi++, flat++)
            {
                if (!qualifying.Contains(flat)) continue;
                float scale = BeadProximity.ScaleForGap(gaps[flat], bead);
                if (scale >= 1f) continue;
                layer.Moves[mi] = layer.Moves[mi] with { WidthScale = scale };
            }
        }

        s_last = [.. runs.Select(r => r.ToPublic())];
    }

    /// <summary>A run under construction — carries the contiguous move count so Apply can reach
    /// every move in it without re-walking.</summary>
    private readonly record struct RunBuild(
        int LayerIndex, float Z, float LengthMm, float ClosestGapMm, float MeanScale,
        bool Corrected, int FirstFlatIndex, int MoveCount)
    {
        public Run ToPublic() =>
            new(LayerIndex, Z, LengthMm, ClosestGapMm, MeanScale, Corrected, FirstFlatIndex);
    }

    /// <summary>
    /// Groups crowded moves into continuous runs. A run ends at a travel, at a non-crowded move, at
    /// a brim move, or at a layer boundary — anywhere the bead genuinely stops running alongside
    /// its neighbour. Contiguous in flat index by construction, which is what lets Apply address
    /// the whole run from its first index and a count.
    /// </summary>
    private static List<RunBuild> FindRuns(
        Toolpath toolpath, float[] gaps, float beadWidthMm, float minRunMm)
    {
        var runs = new List<RunBuild>();

        int    first = -1, count = 0;
        double len = 0.0, scaleSum = 0.0;
        float  closest = float.PositiveInfinity;
        int    layerIndex = 0;
        float  layerZ = 0f;

        void Close()
        {
            if (count > 0)
                runs.Add(new RunBuild(
                    layerIndex, layerZ, (float)len, closest,
                    (float)(scaleSum / count), len >= minRunMm, first, count));
            first = -1; count = 0; len = 0.0; scaleSum = 0.0;
            closest = float.PositiveInfinity;
        }

        int flat = 0;
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer = toolpath.Layers[li];
            layerIndex = li;
            layerZ     = layer.Z;

            foreach (var move in layer.Moves)
            {
                bool crowded = ToolpathMoveKinds.IsCutSegment(move.Kind)
                               && !move.IsBrim
                               && !float.IsNaN(gaps[flat]);

                if (crowded)
                {
                    // Contiguity: a run must be an unbroken index range, so a gap in the indices
                    // starts a new run rather than extending this one.
                    if (count > 0 && flat != first + count) Close();
                    if (count == 0) first = flat;
                    count++;
                    len      += Vector3.Distance(move.From, move.To);
                    scaleSum += BeadProximity.ScaleForGap(gaps[flat], beadWidthMm);
                    if (gaps[flat] < closest) closest = gaps[flat];
                }
                else Close();

                flat++;
            }
            Close();   // a layer boundary ends any open run
        }
        return runs;
    }

    /// <summary>One-line summary for the console and the status bar.</summary>
    public static string Describe(IReadOnlyList<Run> runs, float beadWidthMm)
    {
        if (runs.Count == 0) return "No bead runs alongside another closer than a bead width.";

        int corrected = 0;
        double corrLen = 0.0, skipLen = 0.0;
        float worst = float.PositiveInfinity;
        foreach (var r in runs)
        {
            if (r.Corrected) { corrected++; corrLen += r.LengthMm; }
            else skipLen += r.LengthMm;
            if (r.ClosestGapMm < worst) worst = r.ClosestGapMm;
        }
        return $"{corrected} of {runs.Count} crowded runs corrected ({corrLen / 1000.0:0.###} m of "
             + $"bead; {skipLen / 1000.0:0.###} m left alone as too short to act on). Closest "
             + $"parallel gap {worst:0.##} mm against a {beadWidthMm:0.##} mm bead.";
    }
}
