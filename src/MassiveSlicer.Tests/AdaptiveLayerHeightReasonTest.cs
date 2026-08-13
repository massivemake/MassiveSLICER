using System.Numerics;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

/// <summary>
/// Adaptive layer thickness is the minimum demand of any single triangle crossing that Z,
/// unweighted — triangle area is computed while building the face list and then discarded.
/// So one sliver can pin a whole layer thin, and a layer boundary can be snapped onto a
/// triangle's bottom edge. Both are suspected causes of thickness jumping layer to layer.
///
/// These pin the diagnostic that tells the two apart, and pin that it stays observational:
/// recording a reason must not change the thickness it recorded.
///
/// The fixture is a tall vertical wall (two 5,000 mm² triangles, which impose no constraint
/// because each layer adds a negligible horizontal step) plus ONE near-horizontal sliver of
/// 0.51 mm² sitting at Z 50.0-50.2. The sliver is the only thing in the scene that can
/// demand a thin layer.
/// </summary>
public class AdaptiveLayerHeightReasonTest
{
    private const float MinH  = 1f;
    private const float MaxH  = 3f;
    private const float Quality = 0f;   // tightest tolerance, so the sliver actually bites

    [Fact]
    public void Every_layer_gets_exactly_one_recorded_reason()
    {
        var z = AdaptiveLayerHeights.ComputeZPositions(
            WallWithOneSliver(), 0f, 100f, MaxH, MinH, MaxH, Quality);

        Assert.Equal(z.Length, AdaptiveLayerHeights.LastReasons.Count);
        for (int i = 0; i < z.Length; i++)
            Assert.Equal(z[i], AdaptiveLayerHeights.LastReasons[i].Z, 4);
    }

    /// <summary>
    /// The point of the whole diagnostic: a 0.51 mm² sliver decides a layer against an
    /// average straddling area in the thousands. If this ratio came out near 1 on a real
    /// part, area-weighting the minimum would be a fix for the wrong cause.
    /// </summary>
    [Fact]
    public void A_sliver_that_pins_a_layer_is_recorded_as_tiny_against_what_it_beat()
    {
        AdaptiveLayerHeights.ComputeZPositions(
            WallWithOneSliver(), 0f, 100f, MaxH, MinH, MaxH, Quality);

        var thinnest = AdaptiveLayerHeights.LastReasons.OrderBy(r => r.Height).First();

        Assert.True(thinnest.AtFloor, $"expected the sliver to drive a layer to the floor; got h {thinnest.Height}");
        Assert.True(thinnest.BindingArea > 0f && thinnest.BindingArea < 1f,
            $"expected a sub-1 mm2 deciding triangle, got {thinnest.BindingArea}");
        Assert.True(thinnest.MeanStraddlingArea > 100f,
            $"expected the average straddling face to be large, got {thinnest.MeanStraddlingArea}");
        Assert.True(thinnest.BindingArea / thinnest.MeanStraddlingArea < 0.01f,
            "a sliver deciding a layer must show up as a tiny fraction of average area");
        Assert.InRange(thinnest.BindingSlopeDeg, 5f, 20f);   // the sliver leans ~11 deg off horizontal
    }

    /// <summary>
    /// The second mechanism: rather than choosing a thickness from slope, the walk snaps the
    /// boundary onto the sliver's bottom edge, producing a layer whose thickness is set by
    /// where a vertex happened to land.
    /// </summary>
    [Fact]
    public void A_boundary_snapped_onto_a_triangle_edge_is_flagged_as_such()
    {
        AdaptiveLayerHeights.ComputeZPositions(
            WallWithOneSliver(), 0f, 100f, MaxH, MinH, MaxH, Quality);

        var snapped = AdaptiveLayerHeights.LastReasons.Where(r => r.SnappedToFaceBottom).ToList();

        Assert.NotEmpty(snapped);
        // The snap lands the boundary exactly on the sliver's bottom at Z 50.
        Assert.Contains(snapped, r => MathF.Abs(r.Z + r.Height - 50f) < 1e-3f);
    }

    [Fact]
    public void A_bare_vertical_wall_is_unconstrained_and_reports_no_deciding_triangle()
    {
        AdaptiveLayerHeights.ComputeZPositions(
            WallOnly(), 0f, 60f, MaxH, MinH, MaxH, Quality);

        var reasons = AdaptiveLayerHeights.LastReasons;
        Assert.NotEmpty(reasons);
        Assert.All(reasons, r => Assert.True(r.AtMax, $"a plain wall should take full layers; got {r.Height}"));
        Assert.All(reasons, r => Assert.False(r.SnappedToFaceBottom));
        // Nothing ever beat the starting maximum, so no face was ever recorded as binding.
        Assert.All(reasons, r => Assert.Equal(0f, r.BindingArea, 4));
    }

    /// <summary>
    /// The instrumentation must be observational. Same input, same Z list — and specifically
    /// the sliver fixture must still produce its thin layer, so this is not vacuous.
    /// </summary>
    [Fact]
    public void Recording_reasons_does_not_change_the_heights_chosen()
    {
        var a = AdaptiveLayerHeights.ComputeZPositions(
            WallWithOneSliver(), 0f, 100f, MaxH, MinH, MaxH, Quality);
        var b = AdaptiveLayerHeights.ComputeZPositions(
            WallWithOneSliver(), 0f, 100f, MaxH, MinH, MaxH, Quality);

        Assert.Equal(a, b);

        var heights = new List<float>();
        for (int i = 1; i < a.Length; i++) heights.Add(a[i] - a[i - 1]);
        Assert.Contains(heights, h => MathF.Abs(h - MinH) < 1e-3f);   // the sliver's thin layer is real
        Assert.Contains(heights, h => MathF.Abs(h - MaxH) < 1e-3f);   // the wall's full layers are real
    }

    /// <summary>Two 5,000 mm2 triangles forming a vertical wall in the XZ plane.</summary>
    private static List<Vector3[]> WallOnly() =>
    [
        [
            new Vector3(0, 0, 0),   new Vector3(100, 0, 0),   new Vector3(100, 0, 100),
            new Vector3(0, 0, 0),   new Vector3(100, 0, 100), new Vector3(0, 0, 100),
        ],
    ];

    /// <summary>The wall, plus one 0.51 mm2 near-horizontal sliver spanning Z 50.0-50.2.</summary>
    private static List<Vector3[]> WallWithOneSliver()
    {
        var meshes = WallOnly();
        meshes.Add([
            new Vector3(10, 10, 50.0f), new Vector3(11, 10, 50.0f), new Vector3(10, 11, 50.2f),
        ]);
        return meshes;
    }
}
