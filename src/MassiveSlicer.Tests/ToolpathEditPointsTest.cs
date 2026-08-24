using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

public class ToolpathEditPointsTest
{
    [Fact]
    public void Closed_square_includes_all_four_corners_not_side_midpoints()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f);
        // One long bead per side — old midpoint display put dots in the middle only.
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(80, 0, 10), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(80, 0, 10), new Vector3(80, 80, 10), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(80, 80, 10), new Vector3(0, 80, 10), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 80, 10), new Vector3(0, 0, 10), MoveKind.Extrude));
        layer.Contours.Add(new ContourSpan(0, 4, Closed: true, EntryTravelIndex: -1));
        tp.Layers.Add(layer);

        var pts = ToolpathEditPoints.Collect(tp);
        Assert.Equal(4, pts.Count); // closed: no duplicate close

        var xy = pts.Select(p => (MathF.Round(p.Pos.X), MathF.Round(p.Pos.Y))).ToHashSet();
        Assert.Contains((0, 0), xy);
        Assert.Contains((80, 0), xy);
        Assert.Contains((80, 80), xy);
        Assert.Contains((0, 80), xy);
        Assert.DoesNotContain((40, 0), xy);
        Assert.DoesNotContain((80, 40), xy);
    }

    [Fact]
    public void Subdivided_side_keeps_corners_and_mid_vertices()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0f);
        // 3 points on +X side: (0,0) (40,0) (80,0)
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 0), new Vector3(40, 0, 0), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(40, 0, 0), new Vector3(80, 0, 0), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(80, 0, 0), new Vector3(80, 80, 0), MoveKind.Extrude));
        tp.Layers.Add(layer);

        var pts = ToolpathEditPoints.Collect(tp);
        var xs = pts.Select(p => MathF.Round(p.Pos.X)).ToList();
        Assert.Contains(0f, xs);
        Assert.Contains(40f, xs);
        Assert.Contains(80f, xs);
        Assert.Contains(pts, p => p.Pos.Y > 70f); // last To of open run
    }

    [Fact]
    public void Span_vertices_are_from_plus_each_to()
    {
        var layer = new ToolpathLayer(0, 0f);
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 0), new Vector3(10, 0, 0), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(10, 0, 0), new Vector3(20, 0, 0), MoveKind.Extrude));
        var verts = ToolpathEditPoints.VerticesOfSpan(layer, new ContourSpan(0, 2, false, -1));
        Assert.Equal(3, verts.Count);
        Assert.Equal(0f, verts[0].X, 3);
        Assert.Equal(10f, verts[1].X, 3);
        Assert.Equal(20f, verts[2].X, 3);
    }
}
