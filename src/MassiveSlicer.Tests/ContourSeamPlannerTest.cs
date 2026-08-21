using System.Numerics;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public class ContourSeamPlannerTest
{
    [Fact]
    public void AlignSeamToGuide_rotates_contour_start_near_guide()
    {
        var contour = new List<Vector2>
        {
            new(0, 0), new(100, 0), new(100, 100), new(0, 100),
        };
        var guide = new Vector2(100, 50);
        Vector2 seamRef = new(float.NaN, float.NaN);

        ContourSeamPlanner.AlignSeamToGuide(contour, guide, ref seamRef);

        float dist = Vector2.Distance(contour[0], guide);
        Assert.True(dist < 1f);
    }

    [Fact]
    public void CountCrossings_detects_intersection_with_printed_segment()
    {
        var printed = new List<(Vector2 a, Vector2 b)>
        {
            (new Vector2(50, -10), new Vector2(50, 110)),
        };

        int hits = ContourSeamPlanner.CountCrossings(new Vector2(0, 50), new Vector2(100, 50), printed);
        Assert.Equal(1, hits);
    }

    [Fact]
    public void EmitOptimizedContours_prefers_closer_contour()
    {
        var tracks = new List<PlanarSlicer.ContourTrack>
        {
            new([new Vector2(0, 0), new Vector2(50, 0), new Vector2(50, 50), new Vector2(0, 50)], Vector2.Zero, true),
            new([new Vector2(60, 0), new Vector2(70, 0), new Vector2(70, 10), new Vector2(60, 10)], Vector2.Zero, true),
        };
        var layer = new MassiveSlicer.Core.Models.ToolpathLayer(0, 5f);

        ContourSeamPlanner.EmitOptimizedContours(tracks, 5f, layer, zigZag: false, layerIndex: 0);

        var travel = layer.Moves.FirstOrDefault(m => m.Kind == MassiveSlicer.Core.Models.MoveKind.Travel);
        Assert.NotEqual(default, travel);
        Assert.True(travel.To.X > 55f);
        Assert.True(travel.To.X < 75f);
    }

    /// <summary>
    /// ⭐ A short step between two contours must KEEP PRINTING, not become a travel.
    ///
    /// <para>Under Caracol URM a travel is expensive: extruder off, decelerate to an exact stop,
    /// <c>WAIT SEC 0.5</c>, move, restart, <c>WAIT SEC 0.15</c>. Measured on
    /// Cow_Collumn_Bottom_01, whose arm walls slice into two separate contours: <b>226 travels in the
    /// whole part, 224 of them exactly 6.00 mm, every one of the 226 immediately after a &gt;300 mm arm
    /// wall.</b> In the matching export that was 206 dead stops and 137 s of pure WAIT — the hang seen
    /// on the machine. With the threshold applied the same part slices to <b>2</b> travels, both
    /// genuinely long.</para>
    ///
    /// <para>The layer-change step in <c>PlanarSlicer</c> has always had this rule; only the
    /// within-layer step was missing it.</para>
    /// </summary>
    [Fact]
    public void A_short_step_between_contours_keeps_printing_instead_of_travelling()
    {
        // Two 100 mm open walls 6 mm apart, as separate contours — an arm and its return.
        static List<PlanarSlicer.ContourTrack> Tracks() =>
        [
            new([new Vector2(0f, 0f), new Vector2(100f, 0f)], Vector2.Zero, false),
            new([new Vector2(0f, 6f), new Vector2(100f, 6f)], Vector2.Zero, false),
        ];

        // CONTROL: threshold 0 is the original behaviour — the 6 mm step becomes a travel. Without
        // this half the test would pass on a planner that never emitted a travel in the first place.
        var before = new MassiveSlicer.Core.Models.ToolpathLayer(0, 4f);
        ContourSeamPlanner.EmitOptimizedContours(
            Tracks(), 4f, before, zigZag: false, layerIndex: 0, stitchMaxXyMm: 0f);
        Assert.Contains(before.Moves,
            m => m.Kind == MassiveSlicer.Core.Models.MoveKind.Travel);

        // Bead width 8 mm: the 6 mm step is close enough to keep extruding.
        var after = new MassiveSlicer.Core.Models.ToolpathLayer(0, 4f);
        ContourSeamPlanner.EmitOptimizedContours(
            Tracks(), 4f, after, zigZag: false, layerIndex: 0, stitchMaxXyMm: 8f);

        Assert.DoesNotContain(after.Moves,
            m => m.Kind == MassiveSlicer.Core.Models.MoveKind.Travel);

        // The connector is real bead, and deliberately NOT flagged IsWall (it is not skin) nor
        // IsLayerStitch (that means between-layers, and post-processing effects skip those — this
        // connector must carry flow corrections like any other move).
        var connector = after.Moves.Single(
            m => !m.IsWall && m.Kind == MassiveSlicer.Core.Models.MoveKind.Extrude);
        Assert.False(connector.IsLayerStitch);
        Assert.Equal(6f, Vector2.Distance(new Vector2(connector.From.X, connector.From.Y),
                                          new Vector2(connector.To.X, connector.To.Y)), 3);

        // And a LONG step is still a travel — the threshold must discriminate, not just disable.
        var far = new MassiveSlicer.Core.Models.ToolpathLayer(0, 4f);
        ContourSeamPlanner.EmitOptimizedContours(
        [
            new([new Vector2(0f, 0f), new Vector2(100f, 0f)], Vector2.Zero, false),
            new([new Vector2(0f, 500f), new Vector2(100f, 500f)], Vector2.Zero, false),
        ], 4f, far, zigZag: false, layerIndex: 0, stitchMaxXyMm: 8f);
        Assert.Contains(far.Moves, m => m.Kind == MassiveSlicer.Core.Models.MoveKind.Travel);
    }
}