using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// The support CHECK is not the same question as the support heatmap.
///
/// The heatmap answers "how far off is this bead", on an absolute 0..1 scale of bead width.
/// The check answers "did the slicer meet the overlap target you asked for" — and to be worth
/// looking at, it has to agree with what <see cref="SupportDrivenLayerHeights"/> actually acted
/// on. That means two things the heatmap gets wrong:
///
/// <list type="number">
/// <item>The threshold is the TARGET, not a fixed fraction of bead width. On a 6 mm bead a 3 mm
/// offset is 50 % overlap — dead on target if the target is 50 %, yet the old heatmap paints it
/// mid-red.</item>
/// <item>A stretch shorter than the bridge tolerance is one the slicer deliberately let go
/// (<c>Worst()</c> skips it), so flagging it as a failure shows the operator a problem the
/// feature chose not to solve.</item>
/// </list>
/// </summary>
public class BeadSupportCheckTest
{
    private const float Bead = 6f;

    /// <summary>Half-overlap target on a 6 mm bead: 6 x (1 - 0.5).</summary>
    private const float Target = 3f;

    /// <summary>Generous, so run length is what these tests control rather than the tolerance.</summary>
    private const float Bridge = 12f;

    [Fact]
    public void A_bead_inside_the_target_passes_even_though_it_is_visibly_off()
    {
        // 2.5 mm off a 6 mm bead. The old heatmap paints this ~42 % red; the target says it is fine.
        var tp = Stack(0f, 2.5f);

        var r = BeadSupport.Check(tp, Bead, Target, Bridge);

        Assert.Equal(BeadSupport.SupportVerdict.OnTarget, r.Verdict[1]);
        Assert.False(r.HasFailures);
        Assert.Equal(0f, r.ExtrudedMmFailed, 3);
        Assert.Equal(0f, r.ExtrudedMmPastTarget, 3);
    }

    [Fact]
    public void A_long_stretch_past_the_target_fails()
    {
        var tp = Stack(0f, 4f);          // 4 mm off, over the full 50 mm line

        var r = BeadSupport.Check(tp, Bead, Target, Bridge);

        Assert.Equal(BeadSupport.SupportVerdict.Failed, r.Verdict[1]);
        Assert.True(r.HasFailures);
        Assert.Equal(50f, r.ExtrudedMmFailed, 2);
        Assert.Equal(4f, r.Failures[0].WorstOffsetMm, 3);
        Assert.Equal(50f, r.Failures[0].LengthMm, 2);
    }

    /// <summary>
    /// The distinction the whole view rests on. Same offset, same geometry, different RUN LENGTH —
    /// and the slicer only acts on the long one, so the view must only redden the long one.
    /// </summary>
    [Fact]
    public void The_same_offset_is_bridged_when_short_and_failed_when_long()
    {
        // 8 mm of bad bead against a 12 mm bridge tolerance -> bridged.
        var shortRun = OffsetPatch(badLengthMm: 8f);
        var rShort   = BeadSupport.Check(shortRun, Bead, Target, Bridge);

        // 20 mm of the identical offset -> failed.
        var longRun = OffsetPatch(badLengthMm: 20f);
        var rLong   = BeadSupport.Check(longRun, Bead, Target, Bridge);

        Assert.Contains(BeadSupport.SupportVerdict.Bridged, rShort.Verdict);
        Assert.DoesNotContain(BeadSupport.SupportVerdict.Failed, rShort.Verdict);
        Assert.False(rShort.HasFailures);

        Assert.Contains(BeadSupport.SupportVerdict.Failed, rLong.Verdict);
        Assert.True(rLong.HasFailures);

        // Both are past target — the difference is purely whether it is actionable.
        Assert.True(rShort.ExtrudedMmPastTarget > 0f);
        Assert.True(rLong.ExtrudedMmPastTarget > 0f);
        Assert.Equal(0f, rShort.ExtrudedMmFailed, 3);
        Assert.True(rLong.ExtrudedMmFailed > 0f);
    }

    /// <summary>
    /// Every bead in a failing run is marked failed, not just the worst one. The verdict is a
    /// property of the run: an operator looking at the part needs to see the whole stretch that
    /// will not stick, not a single reddest segment inside it.
    /// </summary>
    [Fact]
    public void The_verdict_applies_to_the_whole_run_not_only_its_worst_bead()
    {
        var below = Layer(0, 0f);
        Seg(below, 0f, 0f, 100f, 0f);

        var above = Layer(1, 3f);
        // Four 10 mm segments walking further and further off: 3.5, 4, 4.5, 5 mm.
        Seg(above, 0f,  3.5f, 10f, 3.5f);
        Seg(above, 10f, 4.0f, 20f, 4.0f);
        Seg(above, 20f, 4.5f, 30f, 4.5f);
        Seg(above, 30f, 5.0f, 40f, 5.0f);

        var tp = new Toolpath();
        tp.Layers.Add(below); tp.Layers.Add(above);

        var r = BeadSupport.Check(tp, Bead, Target, Bridge);

        // 40 mm of continuous bad bead against a 12 mm tolerance — one run, all four failed.
        Assert.Equal(4, r.Verdict.Count(v => v == BeadSupport.SupportVerdict.Failed));
        Assert.Single(r.Failures);
        Assert.Equal(40f, r.Failures[0].LengthMm, 2);
        Assert.Equal(5f, r.Failures[0].WorstOffsetMm, 3);   // the run reports its worst
    }

    /// <summary>
    /// A travel lifts the nozzle, so the stretch genuinely ends there. Without this, two short
    /// bridgeable patches either side of a travel would be welded into one long "failure".
    /// </summary>
    [Fact]
    public void A_travel_move_breaks_a_run_rather_than_joining_two_patches()
    {
        var below = Layer(0, 0f);
        Seg(below, 0f, 0f, 100f, 0f);

        var above = Layer(1, 3f);
        Seg(above, 0f, 4f, 8f, 4f);                       // 8 mm bad
        above.Moves.Add(new ToolpathMove(                  // lift and move on
            new Vector3(8, 4, 3), new Vector3(40, 4, 3), MoveKind.Travel));
        Seg(above, 40f, 4f, 48f, 4f);                     // another 8 mm bad

        var tp = new Toolpath();
        tp.Layers.Add(below); tp.Layers.Add(above);

        var r = BeadSupport.Check(tp, Bead, Target, Bridge);

        // Two 8 mm runs, each under the 12 mm tolerance. Welded together they would be 16 mm
        // and would wrongly fail.
        Assert.False(r.HasFailures);
        Assert.Equal(2, r.Verdict.Count(v => v == BeadSupport.SupportVerdict.Bridged));
    }

    /// <summary>
    /// The reason <see cref="BeadSupport.ReportingSearchRings"/> exists. A 3x3 scan on a
    /// bead-width grid cannot see past one cell, so it reports infinity and anything that
    /// DIVIDES by the distance gets nonsense — this is the "would need h 0 mm" artifact that
    /// reads as floating geometry on a part where nothing is floating.
    /// </summary>
    [Fact]
    public void Far_offsets_are_measured_as_real_millimetres_not_collapsed_to_infinity()
    {
        var tp = Stack(0f, 14.17f);       // the figure brute force found on a real part

        // The default 3x3 window: past one 6 mm cell, so it can only say "too far".
        var narrow = BeadSupport.MeasureOffsets(tp, Bead);
        Assert.True(float.IsPositiveInfinity(narrow[1]),
            "a 3x3 scan cannot see 14 mm on a 6 mm grid — this is the artifact, pinned here");

        // The reporting window measures it.
        var wide = BeadSupport.MeasureOffsets(tp, Bead, BeadSupport.ReportingSearchRings);
        Assert.Equal(14.17f, wide[1], 2);

        // And Check uses the wide one, so the number it reports is real.
        var r = BeadSupport.Check(tp, Bead, Target, Bridge);
        Assert.Equal(14.17f, r.Failures[0].WorstOffsetMm, 2);
    }

    /// <summary>
    /// The ring walk must not change what the slicer decides. Existing callers keep the 3x3
    /// default, so the heatmap and every layer-height decision stay bit-identical.
    /// </summary>
    [Fact]
    public void Widening_the_search_is_opt_in_and_the_default_is_unchanged()
    {
        Assert.Equal(1, BeadSupport.DefaultSearchRings);

        var tp = new Toolpath();
        tp.Layers.Add(Line(0, 0f, 0f));
        tp.Layers.Add(Line(1, 3f, 1.5f));
        tp.Layers.Add(Line(2, 6f, 400f));      // beyond any window

        var explicitDefault = BeadSupport.MeasureOffsets(tp, Bead, BeadSupport.DefaultSearchRings);
        var implicitDefault = BeadSupport.MeasureOffsets(tp, Bead);

        for (int i = 0; i < implicitDefault.Length; i++)
            Assert.Equal(implicitDefault[i], explicitDefault[i], 5);

        // Truly-far geometry is still infinity even at the wide setting — widening reaches
        // further, it does not invent a measurement.
        var wide = BeadSupport.MeasureOffsets(tp, Bead, BeadSupport.ReportingSearchRings);
        Assert.True(float.IsPositiveInfinity(wide[2]));

        // A near bead reads the same at both widths: expanding the ring cannot move a hit that
        // the inner ring already found.
        Assert.Equal(1.5f, wide[1], 4);
    }

    /// <summary>
    /// The ring walk early-exits, and the exit must happen AFTER the ring is scanned. Exiting
    /// on ring 0 the moment anything is found would return a segment in the point's own cell
    /// while a nearer one sits just across the cell boundary.
    /// </summary>
    [Fact]
    public void The_ring_walk_still_finds_the_nearest_segment_across_a_cell_boundary()
    {
        var below = Layer(0, 0f);
        // Far segment inside the query point's own cell, near segment in the next cell over.
        Seg(below, 0f, 5.5f, 6f, 5.5f);     // ~5.5 mm away, same cell as the point
        Seg(below, 0f, -0.4f, 6f, -0.4f);   // 0.4 mm away, the cell below

        var above = Layer(1, 3f);
        Seg(above, 1f, 0f, 5f, 0f);

        var tp = new Toolpath();
        tp.Layers.Add(below); tp.Layers.Add(above);

        var wide = BeadSupport.MeasureOffsets(tp, Bead, BeadSupport.ReportingSearchRings);
        Assert.Equal(0.4f, wide[2], 3);
    }

    [Fact]
    public void Scores_land_in_their_own_band_so_a_pass_never_looks_like_a_failure()
    {
        var below = Layer(0, 0f);
        Seg(below, 0f, 0f, 200f, 0f);

        var above = Layer(1, 3f);
        Seg(above, 0f,   0f,   50f,  0f);     // stacked      -> OnTarget
        Seg(above, 50f,  2.9f, 100f, 2.9f);   // just inside  -> OnTarget
        Seg(above, 100f, 4f,   106f, 4f);     // 6 mm bad     -> Bridged
        Seg(above, 106f, 0f,   150f, 0f);     // back on target, closing the run
        Seg(above, 150f, 4f,   200f, 4f);     // 50 mm bad    -> Failed

        var tp = new Toolpath();
        tp.Layers.Add(below); tp.Layers.Add(above);

        var r = BeadSupport.Check(tp, Bead, Target, Bridge);
        var s = BeadSupport.CheckScores(r);

        float ScoreOf(BeadSupport.SupportVerdict v)
        {
            for (int i = 0; i < r.Verdict.Length; i++) if (r.Verdict[i] == v) return s[i];
            throw new Xunit.Sdk.XunitException($"no move with verdict {v} — test is vacuous");
        }

        // Every class is present, so none of the assertions below pass by absence.
        Assert.True(ScoreOf(BeadSupport.SupportVerdict.OnTarget) <= BeadSupport.BandOnTargetMax);
        float bridged = ScoreOf(BeadSupport.SupportVerdict.Bridged);
        Assert.InRange(bridged, BeadSupport.BandBridgedMin, BeadSupport.BandBridgedMax);
        Assert.True(ScoreOf(BeadSupport.SupportVerdict.Failed) >= BeadSupport.BandFailedMin);

        // The bands do not touch — that gap is what makes the boundary readable.
        Assert.True(BeadSupport.BandOnTargetMax < BeadSupport.BandBridgedMin);
        Assert.True(BeadSupport.BandBridgedMax < BeadSupport.BandFailedMin);
    }

    [Fact]
    public void Worst_failure_sorts_first_so_the_report_leads_with_it()
    {
        var below = Layer(0, 0f);
        Seg(below, 0f, 0f, 300f, 0f);

        var above = Layer(1, 3f);
        Seg(above, 0f,   4f, 20f,  4f);    // 20 mm bad
        Seg(above, 20f,  0f, 100f, 0f);    // good, closes it
        Seg(above, 100f, 4f, 200f, 4f);    // 100 mm bad — the worst
        Seg(above, 200f, 0f, 220f, 0f);
        Seg(above, 220f, 4f, 260f, 4f);    // 40 mm bad

        var tp = new Toolpath();
        tp.Layers.Add(below); tp.Layers.Add(above);

        var r = BeadSupport.Check(tp, Bead, Target, Bridge);

        Assert.Equal(3, r.Failures.Count);
        Assert.Equal(100f, r.Failures[0].LengthMm, 2);
        Assert.Equal(40f,  r.Failures[1].LengthMm, 2);
        Assert.Equal(20f,  r.Failures[2].LengthMm, 2);
        Assert.Equal(160f, r.ExtrudedMmFailed, 2);
        Assert.Equal(1, r.Failures[0].LayerIndex);
        Assert.Equal(3f, r.Failures[0].Z, 3);
    }

    [Fact]
    public void First_layer_is_never_a_failure_and_percentages_stay_finite()
    {
        var tp = new Toolpath();
        tp.Layers.Add(Line(0, 0f, 0f));          // nothing beneath it

        var r = BeadSupport.Check(tp, Bead, Target, Bridge);

        Assert.Equal(BeadSupport.SupportVerdict.NotMeasured, r.Verdict[0]);
        Assert.False(r.HasFailures);
        Assert.Equal(0f, r.FailedPercent, 4);
        Assert.Equal(0f, r.PastTargetPercent, 4);

        // Unmeasured bead still colours as the safe end, matching the old heatmap's treatment.
        Assert.Equal(0f, BeadSupport.CheckScores(r)[0], 5);
    }

    [Fact]
    public void Degenerate_inputs_return_empty_rather_than_dividing_by_zero()
    {
        var tp = Stack(0f, 4f);

        Assert.False(BeadSupport.Check(new Toolpath(), Bead, Target, Bridge).HasFailures);
        Assert.False(BeadSupport.Check(tp, 0f, Target, Bridge).HasFailures);
        Assert.False(BeadSupport.Check(tp, Bead, 0f, Bridge).HasFailures);

        // A zero bridge tolerance is meaningful, not degenerate: nothing bridges, so any
        // stretch past target fails.
        var strict = BeadSupport.Check(tp, Bead, Target, 0f);
        Assert.True(strict.HasFailures);
    }

    // -- helpers ---------------------------------------------------------------------------

    private static ToolpathLayer Layer(int index, float z)
        => new(index, z) { Height = 3f, PlaneNormal = Vector3.UnitZ };

    private static void Seg(ToolpathLayer layer, float x0, float y0, float x1, float y1)
        => layer.Moves.Add(new ToolpathMove(
            new Vector3(x0, y0, layer.Z), new Vector3(x1, y1, layer.Z), MoveKind.Extrude));

    private static ToolpathLayer Line(int index, float z, float y)
    {
        var layer = Layer(index, z);
        Seg(layer, 0f, y, 50f, y);
        return layer;
    }

    /// <summary>Two 50 mm lines, the upper stepped sideways by <paramref name="offsetMm"/>.</summary>
    private static Toolpath Stack(float baseY, float offsetMm)
    {
        var tp = new Toolpath();
        tp.Layers.Add(Line(0, 0f, baseY));
        tp.Layers.Add(Line(1, 3f, baseY + offsetMm));
        return tp;
    }

    /// <summary>
    /// A 100 mm layer sitting square on the one below, except for one patch of the given length
    /// stepped 4 mm off. Run length is the only thing that varies.
    /// </summary>
    private static Toolpath OffsetPatch(float badLengthMm)
    {
        var below = Layer(0, 0f);
        Seg(below, 0f, 0f, 100f, 0f);

        var above = Layer(1, 3f);
        Seg(above, 0f, 0f, 20f, 0f);                              // good
        Seg(above, 20f, 4f, 20f + badLengthMm, 4f);               // bad patch
        Seg(above, 20f + badLengthMm, 0f, 100f, 0f);              // good again

        var tp = new Toolpath();
        tp.Layers.Add(below); tp.Layers.Add(above);
        return tp;
    }
}
