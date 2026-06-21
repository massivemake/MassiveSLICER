using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public class ToolpathMergerTest
{
    [Fact]
    public void Merge_inserts_z_hop_connector_between_toolpaths()
    {
        var tp1 = MakeToolpath([(0, 0, 10), (50, 0, 10)]);
        var tp2 = MakeToolpath([(200, 0, 10), (250, 0, 10)]);

        var merged = ToolpathMerger.Merge([tp1, tp2], retractionHeightMm: 5f, travelSpeedMps: 0.25f);
        var moves  = merged.Layers[0].Moves;

        Assert.Equal(1 + 3 + 1, moves.Count);
        Assert.True(moves[1].IsMergeConnector);
        Assert.Equal(3, moves.Skip(1).Take(3).Count(m => m.IsZHop));
        Assert.All(moves.Where(m => m.IsMergeConnector), m => Assert.Equal(0.25f, m.TravelSpeedMps));
    }

    [Fact]
    public void Merge_preserves_toolpath_order()
    {
        var tp1 = MakeToolpath([(0, 0, 0), (10, 0, 0)]);
        var tp2 = MakeToolpath([(100, 0, 0), (110, 0, 0)]);
        var tp3 = MakeToolpath([(200, 0, 0), (210, 0, 0)]);

        var merged = ToolpathMerger.Merge([tp1, tp2, tp3], retractionHeightMm: 0f, travelSpeedMps: 0.5f);
        var xs = merged.Layers[0].Moves.Where(m => m.Kind == MoveKind.Extrude).Select(m => m.To.X).ToList();

        Assert.Equal([10f, 110f, 210f], xs);
        Assert.Equal(2, merged.Layers[0].Moves.Count(m => m.IsMergeConnector && !m.IsZHop));
    }

    private static Toolpath MakeToolpath((float x, float y, float z)[] points)
    {
        var layer = new ToolpathLayer(0, points[0].z) { PlaneNormal = Vector3.UnitZ };
        for (int i = 1; i < points.Length; i++)
        {
            var from = new Vector3(points[i - 1].x, points[i - 1].y, points[i - 1].z);
            var to   = new Vector3(points[i].x, points[i].y, points[i].z);
            layer.Moves.Add(new ToolpathMove(from, to, MoveKind.Extrude) { Normal = Vector3.UnitZ });
        }

        var tp = new Toolpath();
        tp.Layers.Add(layer);
        return tp;
    }
}