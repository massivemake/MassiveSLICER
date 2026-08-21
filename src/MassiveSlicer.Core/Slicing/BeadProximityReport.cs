using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Groups the per-move clearances from <see cref="BeadProximity"/> into continuous RUNS, so a part
/// with four internal arms reports four findings rather than a cloud of moves.
///
/// <para><b>Measurement only, by design.</b> Nothing here changes a toolpath. It does not stamp a
/// scale on a move, the slicer does not call it, and no export reads it. It answers "where do beads
/// on this layer run alongside each other, for how far, and how close" and stops there. What to do
/// about a detected arm is a separate decision that has not been made.</para>
///
/// <para><b>Why runs and not moves.</b> A verdict has to belong to the whole stretch: a bead
/// alongside another for 12 mm — the outer wall clipping past the end of an arm — is a different
/// finding from one alongside for 600 mm, even though the individual moves look identical. Measured
/// on a real part the two populations separate with nothing in between: 2237 runs under 15 mm
/// against 784 runs of 360-366 mm, and an empty band from 60 to 250 mm wide. Any threshold inside
/// that band gives the same answer, which is why <see cref="DefaultLongRunMm"/> is not a tuning
/// knob.</para>
/// </summary>
public static class BeadProximityReport
{
    /// <summary>
    /// A run at least this long is a real parallel feature rather than an incidental near-miss.
    /// Sits in the empty band between the two measured populations, so its exact value is not
    /// load-bearing.
    /// </summary>
    public const float DefaultLongRunMm = 100f;

    /// <summary>One continuous stretch of bead running alongside a neighbour on the same layer.</summary>
    /// <param name="ClosestGapMm">Tightest clearance anywhere in the run.</param>
    /// <param name="MeanGapMm">Average clearance, so a run that tapers is distinguishable.</param>
    /// <param name="IsLongRun">Past <see cref="DefaultLongRunMm"/> — a feature, not a near-miss.</param>
    public sealed record Run(
        int LayerIndex,
        float Z,
        float LengthMm,
        float ClosestGapMm,
        float MeanGapMm,
        bool IsLongRun,
        int FirstFlatIndex,
        int MoveCount);

    /// <summary>
    /// Measures <paramref name="toolpath"/> and returns every crowded run, longest first. A pure
    /// function — no static state to go stale, and nothing to enable. Safe to call any time.
    /// </summary>
    public static IReadOnlyList<Run> Measure(
        Toolpath toolpath, float beadWidthMm, float longRunMm = DefaultLongRunMm)
    {
        if (beadWidthMm <= 0f || toolpath.Layers.Count == 0) return [];

        var gaps = BeadProximity.MeasureGaps(toolpath, beadWidthMm);
        float minRun = MathF.Max(longRunMm, 0f);
        var runs = new List<Run>();

        int   first = -1, count = 0;
        double len = 0.0, gapSum = 0.0;
        float closest = float.PositiveInfinity;
        int   layerIndex = 0;
        float layerZ = 0f;
        Vector3? prevEnd = null;   // where the open run's last bead finished

        void Close()
        {
            if (count > 0)
                runs.Add(new Run(
                    layerIndex, layerZ, (float)len, closest,
                    (float)(gapSum / count), len >= minRun, first, count));
            first = -1; count = 0; len = 0.0; gapSum = 0.0;
            closest = float.PositiveInfinity;
            prevEnd = null;
        }

        int flat = 0;
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer  = toolpath.Layers[li];
            layerIndex = li;
            layerZ     = layer.Z;

            foreach (var move in layer.Moves)
            {
                bool crowded = ToolpathMoveKinds.IsCutSegment(move.Kind)
                               && !move.IsBrim
                               && !float.IsNaN(gaps[flat]);

                if (crowded)
                {
                    // A run must be an unbroken index range, so a gap in the indices starts a new
                    // one rather than extending this.
                    if (count > 0 && flat != first + count) Close();

                    // And unbroken in SPACE. Two features whose beads happen to be adjacent in the
                    // move list are not one stretch — merging them would pool their lengths, so two
                    // short near-misses could jointly clear the threshold and both read as features.
                    if (prevEnd is { } pe && Vector3.Distance(pe, move.From) > 1e-3f) Close();

                    if (count == 0) first = flat;
                    count++;
                    len    += Vector3.Distance(move.From, move.To);
                    gapSum += gaps[flat];
                    if (gaps[flat] < closest) closest = gaps[flat];
                    prevEnd = move.To;
                }
                else Close();

                flat++;
            }
            Close();   // a layer boundary ends any open run
        }

        runs.Sort((a, b) => b.LengthMm.CompareTo(a.LengthMm));
        return runs;
    }

    /// <summary>
    /// How much more material a run delivers than the space between the beads holds, as a ratio.
    /// Two passes at pitch <c>p</c> each deliver a bead of width <c>w</c>, so the excess is
    /// <c>w/p - 1</c>. Pure geometry: it says what is there, not what to do about it.
    /// </summary>
    public static float ExcessRatio(float gapMm, float beadWidthMm)
    {
        if (gapMm <= 0f || beadWidthMm <= 0f || gapMm >= beadWidthMm) return 0f;
        return beadWidthMm / gapMm - 1f;
    }

    /// <summary>One-line summary for the console.</summary>
    public static string Describe(IReadOnlyList<Run> runs, float beadWidthMm)
    {
        if (runs.Count == 0) return "No bead runs alongside another closer than a bead width.";

        int features = 0;
        double featureLen = 0.0, shortLen = 0.0;
        float worst = float.PositiveInfinity;
        foreach (var r in runs)
        {
            if (r.IsLongRun) { features++; featureLen += r.LengthMm; }
            else shortLen += r.LengthMm;
            if (r.ClosestGapMm < worst) worst = r.ClosestGapMm;
        }
        return $"{features} of {runs.Count} crowded runs are parallel features "
             + $"({featureLen / 1000.0:0.###} m of bead; {shortLen / 1000.0:0.###} m of incidental "
             + $"near-misses). Closest parallel gap {worst:0.##} mm against a {beadWidthMm:0.##} mm "
             + "bead.";
    }
}
