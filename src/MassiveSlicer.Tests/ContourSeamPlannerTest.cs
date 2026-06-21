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
}