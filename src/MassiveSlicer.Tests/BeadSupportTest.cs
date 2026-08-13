using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// Bead support is the sideways distance from each bead to the material under it — the
/// input for overhang-driven speed and for choosing layer heights, and the number the
/// Bead overhang heatmap draws.
///
/// The measurement it must NOT make is point-to-point. Real layers carry only a few
/// hundred vertices, so beads sit ~12 mm apart along the path; a nearest-vertex search
/// measures polyline spacing rather than geometry, and it once reported 18 % overlap on
/// a layer whose true figure was 44 %. Several tests here are built so a vertex-only
/// implementation fails them.
/// </summary>
public class BeadSupportTest
{
    private const float Bead = 6f;

    [Fact]
    public void Bead_stacked_squarely_on_the_one_below_measures_zero_offset()
    {
        var tp = new Toolpath();
        tp.Layers.Add(Line(0, z: 0f, y: 0f));
        tp.Layers.Add(Line(1, z: 3f, y: 0f));

        var a = BeadSupport.Analyze(tp, Bead);

        Assert.Equal(0f, a.OffsetMm[1], 4);        // second layer, directly above the first
        Assert.Equal(0f, a.FractionAt(1), 4);
        Assert.Equal(100f, a.OverlapPercentAt(1), 3);
    }

    [Fact]
    public void Offset_is_reported_in_mm_and_tracks_the_real_sideways_step()
    {
        var tp = new Toolpath();
        tp.Layers.Add(Line(0, z: 0f, y: 0f));
        tp.Layers.Add(Line(1, z: 3f, y: 2.5f));    // stepped 2.5 mm sideways

        var a = BeadSupport.Analyze(tp, Bead);

        Assert.Equal(2.5f, a.OffsetMm[1], 4);
        // 2.5 of a 6 mm bead hangs off -> 58.33 % still covered.
        Assert.Equal(2.5f / 6f, a.FractionAt(1), 4);
        Assert.Equal(58.333f, a.OverlapPercentAt(1), 2);
    }

    /// <summary>
    /// The trap. The bead below runs from x=0 to x=100 with vertices ONLY at its ends, and
    /// the bead above sits at x=50 — 1 mm up in Y. Measured to the segment the answer is
    /// 1 mm. Measured to the nearest vertex it is ~50 mm, which would clamp to "unsupported"
    /// and report 0 % overlap on a bead that is almost perfectly supported.
    /// </summary>
    [Fact]
    public void Distance_is_measured_to_the_segment_not_to_its_endpoints()
    {
        var below = new ToolpathLayer(0, 0f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        below.Moves.Add(new ToolpathMove(
            new Vector3(0, 0, 0), new Vector3(100, 0, 0), MoveKind.Extrude));

        var above = new ToolpathLayer(1, 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        above.Moves.Add(new ToolpathMove(
            new Vector3(49, 1, 3), new Vector3(51, 1, 3), MoveKind.Extrude));

        var tp = new Toolpath();
        tp.Layers.Add(below);
        tp.Layers.Add(above);

        var a = BeadSupport.Analyze(tp, Bead);

        Assert.Equal(1f, a.OffsetMm[1], 4);
        Assert.True(a.OverlapPercentAt(1) > 80f,
            $"a bead 1 mm off a 6 mm bead is well supported; got {a.OverlapPercentAt(1):0.#} %");
    }

    [Fact]
    public void Bead_with_nothing_under_it_reads_unsupported_and_clamps_the_fraction_to_one()
    {
        var tp = new Toolpath();
        tp.Layers.Add(Line(0, z: 0f, y: 0f));
        tp.Layers.Add(Line(1, z: 3f, y: 500f));    // nowhere near the layer below

        var a = BeadSupport.Analyze(tp, Bead);

        Assert.True(float.IsPositiveInfinity(a.OffsetMm[1]),
            "beyond the searched neighbourhood we cannot say how far — only that it is too far");
        Assert.Equal(1f, a.FractionAt(1), 4);
        Assert.Equal(0f, a.OverlapPercentAt(1), 3);
        Assert.Equal("unsupported", BeadSupport.Mm(a.OffsetMm[1]));
    }

    /// <summary>
    /// The first layer sits on the bed and travel moves have no bead, so both read 0 —
    /// which is what keeps the heatmap unchanged by moving this into Core.
    /// </summary>
    [Fact]
    public void First_layer_and_travel_moves_read_as_fully_supported()
    {
        var first = new ToolpathLayer(0, 0f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        first.Moves.Add(new ToolpathMove(
            new Vector3(0, 0, 0), new Vector3(50, 0, 0), MoveKind.Extrude));

        var second = new ToolpathLayer(1, 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        second.Moves.Add(new ToolpathMove(
            new Vector3(50, 0, 3), new Vector3(0, 400, 3), MoveKind.Travel));
        second.Moves.Add(new ToolpathMove(
            new Vector3(0, 0, 3), new Vector3(50, 0, 3), MoveKind.Extrude));

        var tp = new Toolpath();
        tp.Layers.Add(first);
        tp.Layers.Add(second);

        var a = BeadSupport.Analyze(tp, Bead);

        Assert.Equal(0f, a.OffsetMm[0], 4);   // first layer, on the bed
        Assert.Equal(0f, a.OffsetMm[1], 4);   // the travel move, despite ending 400 mm away
        Assert.Equal(0f, a.OffsetMm[2], 4);   // stacked extrude
        Assert.Equal(1, a.MeasuredMoves);     // only the extrude on layer 1 counts
    }

    /// <summary>
    /// A guard against the whole thing reading zero — the failure mode that would make every
    /// test above pass for the wrong reason and paint the part uniformly white.
    /// </summary>
    [Fact]
    public void A_leaning_wall_produces_a_spread_of_offsets_not_one_constant()
    {
        var tp = new Toolpath();
        for (int i = 0; i < 12; i++)
            tp.Layers.Add(Line(i, z: i * 3f, y: i * 1.4f));   // walks 1.4 mm sideways per layer

        var a = BeadSupport.Analyze(tp, Bead);

        Assert.Equal(11, a.MeasuredMoves);
        Assert.Equal(1.4f, a.MedianMm, 3);
        Assert.Equal(1.4f, a.MaxMm, 3);
        Assert.True(a.TotalExtrudedMm > 0f, "extruded length must be accumulated for weighting");
        Assert.Equal(11, a.Layers.Count);      // every layer above the first gets a stat
    }

    [Fact]
    public void Length_weighting_counts_only_the_badly_supported_bead()
    {
        var tp = new Toolpath();
        tp.Layers.Add(Line(0, z: 0f, y: 0f));
        tp.Layers.Add(Line(1, z: 3f, y: 0f));      // stacked   — 50 mm of good bead
        tp.Layers.Add(Line(2, z: 6f, y: 4f));      // 4 mm off  — 50 mm past half a bead (3 mm)

        var a = BeadSupport.Analyze(tp, Bead);

        Assert.Equal(100f, a.TotalExtrudedMm, 2);
        Assert.Equal(50f, a.ExtrudedMmUnderHalfOverlap, 2);
        Assert.Equal(50f, a.ExtrudedMmUnderThreeQuarterOverlap, 2);
    }

    [Fact]
    public void Empty_toolpath_and_zero_bead_width_return_empty_rather_than_dividing_by_zero()
    {
        Assert.Equal(0, BeadSupport.Analyze(new Toolpath(), Bead).MeasuredMoves);

        var tp = new Toolpath();
        tp.Layers.Add(Line(0, z: 0f, y: 0f));
        tp.Layers.Add(Line(1, z: 3f, y: 0f));
        Assert.Equal(0, BeadSupport.Analyze(tp, 0f).MeasuredMoves);
    }

    /// <summary>One 50 mm bead along X at the given Y and Z.</summary>
    private static ToolpathLayer Line(int index, float z, float y)
    {
        var layer = new ToolpathLayer(index, z) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(
            new Vector3(0, y, z), new Vector3(50, y, z), MoveKind.Extrude));
        return layer;
    }
}
