using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Chooses layer thickness from MEASURED overlap instead of from triangle normals.
///
/// <see cref="AdaptiveLayerHeights"/> answers a surface-FINISH question — would this thickness
/// leave a visible staircase on a shallow surface — by reading facet normals. This answers an
/// ADHESION question: will the bead actually sit on the one below. Different question, different
/// data. The two compose by taking the thinner.
///
/// Why this reads the boundary rather than normals: a normal is the derivative of the surface, and
/// differentiating amplifies tessellation noise (see the min-face-area gate, which exists purely
/// to stop sub-bead slivers deciding thicknesses). Contour positions are accurate to microns. The
/// sideways step between two contours IS the overhang, rather than a proxy for it.
///
/// <para><b>The circularity, and how it is broken.</b> Thickness sets where the next slice plane
/// goes, which sets the next contour, which is what you must measure to choose the thickness. The
/// way out is that for a locally planar surface the sideways step is LINEAR in thickness — so
/// measuring a trial step at thickness h and finding offset s tells you the thickness that hits
/// target s* directly: <c>h* = h × s*/s</c>. One multiply, not a search. This walks up one layer
/// at a time, proposing, measuring once, correcting once.</para>
/// </summary>
public static class SupportDrivenLayerHeights
{
    /// <summary>What happened at one layer. Diagnostics; nothing reads it to decide.</summary>
    public sealed record Decision(
        float Z,
        float ProposedThicknessMm,
        float FinalThicknessMm,
        float WorstOffsetMm,
        float LongestRunMm,
        bool  Thinned,
        bool  Unfixable,
        float NeededThicknessMm);

    /// <summary>Decisions from the most recent completed <see cref="Refine"/>, published atomically.</summary>
    public static IReadOnlyList<Decision> LastDecisions => s_last;
    private static Decision[] s_last = [];

    /// <summary>
    /// Rewrites a slice-plane ladder so no continuous stretch longer than the bridging tolerance
    /// sits further off the layer below than the target.
    /// </summary>
    /// <param name="proposed">
    /// The ladder the finish criterion produced (or a uniform one). Used as the thickness this
    /// walk starts from at each step, sampled at the current height — so the finish criterion
    /// still gets its say and this can only ever make a layer THINNER, never thicker.
    /// </param>
    /// <param name="contoursAt">Slices the mesh at one Z. Called once or twice per output layer.</param>
    public static float[] Refine(
        float[] proposed,
        float zMax,
        Func<float, IReadOnlyList<IReadOnlyList<Vector2>>> contoursAt,
        float targetOffsetMm,
        float bridgeToleranceMm,
        float minLayerHeight,
        float maxLayerHeight,
        float searchCellMm)
    {
        if (proposed.Length < 2 || targetOffsetMm <= 0f)
        {
            s_last = [];
            return proposed;
        }

        var decisions = new List<Decision>();
        var outZ      = new List<float> { proposed[0] };

        float z    = proposed[0];
        var   prev = contoursAt(z);
        int   guard = proposed.Length * 4 + 64;   // a thinning walk emits more layers than it was given

        while (z < zMax - 1e-4f && outZ.Count < guard)
        {
            float h = Math.Clamp(ProposedThicknessAt(proposed, z), minLayerHeight, maxLayerHeight);

            var  trial = contoursAt(z + h);
            var  runs  = BeadSupport.ContourOffsetRuns(trial, prev, targetOffsetMm, searchCellMm);
            var  bad   = Worst(runs, bridgeToleranceMm);

            float finalH = h, needed = h;
            bool thinned = false, unfixable = false;

            if (bad is { } b)
            {
                // Linear rescale: offset scales with thickness, so this lands on target directly.
                needed = h * targetOffsetMm / b.WorstOffsetMm;
                finalH = Math.Clamp(needed, minLayerHeight, h);   // only ever thinner
                thinned = finalH < h - 1e-4f;

                if (thinned)
                {
                    // Re-measure against a hair above target. The rescale aims to land exactly ON
                    // target, so an exact comparison trips on float rounding and would report every
                    // successful correction as a failure.
                    trial = contoursAt(z + finalH);
                    runs  = BeadSupport.ContourOffsetRuns(
                        trial, prev, targetOffsetMm * 1.001f, searchCellMm);
                }
                // Still over target after clamping to the floor: genuinely unsupported geometry,
                // not a thickness problem. Recorded so it can be reported rather than hidden.
                unfixable = Worst(runs, bridgeToleranceMm) is not null;
            }

            var after = Worst(runs, bridgeToleranceMm);
            decisions.Add(new Decision(
                z, h, finalH,
                after?.WorstOffsetMm ?? bad?.WorstOffsetMm ?? 0f,
                after?.LengthMm ?? bad?.LengthMm ?? 0f,
                thinned, unfixable, needed));

            z += finalH;
            outZ.Add(z);
            prev = trial;
        }

        s_last = [.. decisions];
        return [.. outZ];
    }

    /// <summary>The stretch that forces the decision: longest past tolerance, worst offset of those.</summary>
    private static BeadSupport.OffsetRun? Worst(List<BeadSupport.OffsetRun> runs, float toleranceMm)
    {
        BeadSupport.OffsetRun? worst = null;
        foreach (var r in runs)
        {
            if (r.LengthMm <= toleranceMm) continue;      // a bead bridges this
            if (worst is null || r.WorstOffsetMm > worst.WorstOffsetMm) worst = r;
        }
        return worst;
    }

    /// <summary>
    /// The finish criterion's thickness near this height. The ladder shifts as layers are thinned,
    /// so its entries no longer line up with where the walk actually is; thickness varies smoothly
    /// with Z, so sampling the nearest original step is accurate enough to start from.
    /// </summary>
    private static float ProposedThicknessAt(float[] ladder, float z)
    {
        for (int i = 1; i < ladder.Length; i++)
            if (ladder[i] > z + 1e-4f)
                return ladder[i] - ladder[i - 1];
        return ladder[^1] - ladder[^2];
    }

    /// <summary>One-line summary for the console and the status bar.</summary>
    public static string Describe(IReadOnlyList<Decision> d)
    {
        if (d.Count == 0) return "Support-driven layer height did not run.";
        int thinned = 0, unfixable = 0;
        foreach (var x in d) { if (x.Thinned) thinned++; if (x.Unfixable) unfixable++; }
        return $"{d.Count} layers, {thinned} thinned for overlap, " +
               $"{unfixable} still short at the minimum layer height.";
    }
}
