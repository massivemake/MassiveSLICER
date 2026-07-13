using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

public class ContourPointPathTest
{
    private static ToolpathLayer OpenLine(int n)
    {
        var layer = new ToolpathLayer(0, 0f);
        for (int i = 0; i < n; i++)
        {
            var a = new Vector3(i * 10f, 0, 0);
            var b = new Vector3((i + 1) * 10f, 0, 0);
            layer.Moves.Add(new ToolpathMove(a, b, MoveKind.Extrude));
        }
        layer.Contours.Add(new ContourSpan(0, n, Closed: false, EntryTravelIndex: -1));
        return layer;
    }

    private static ToolpathLayer ClosedLoop(int n)
    {
        var layer = new ToolpathLayer(0, 0f);
        for (int i = 0; i < n; i++)
        {
            float a0 = MathF.Tau * i / n;
            float a1 = MathF.Tau * (i + 1) / n;
            var a = new Vector3(50f * MathF.Cos(a0), 50f * MathF.Sin(a0), 0);
            var b = new Vector3(50f * MathF.Cos(a1), 50f * MathF.Sin(a1), 0);
            layer.Moves.Add(new ToolpathMove(a, b, MoveKind.Extrude));
        }
        layer.Contours.Add(new ContourSpan(0, n, Closed: true, EntryTravelIndex: -1));
        return layer;
    }

    private static int TotalMoves(IReadOnlyList<ContourSpan> spans)
    {
        int t = 0;
        foreach (var s in spans) t += s.Count;
        return t;
    }

    [Fact]
    public void OpenPath_SelectsInclusiveInterval()
    {
        var layer = OpenLine(20);
        var path = ContourPointPath.ShortestPath(layer, 3, 10);
        Assert.Single(path);
        Assert.Equal(3, path[0].Start);
        Assert.Equal(8, path[0].Count); // 3..10 inclusive
        Assert.Equal(8, TotalMoves(path));
    }

    [Fact]
    public void OpenPath_OrderIndependent()
    {
        var layer = OpenLine(12);
        var ab = ContourPointPath.ShortestPath(layer, 2, 8);
        var ba = ContourPointPath.ShortestPath(layer, 8, 2);
        Assert.Equal(ab[0].Start, ba[0].Start);
        Assert.Equal(ab[0].Count, ba[0].Count);
    }

    [Fact]
    public void ClosedLoop_PicksShorterArc()
    {
        var layer = ClosedLoop(20);
        // 1 → 4 forward is 3 steps (4 points); long way is 17 — take short.
        var path = ContourPointPath.ShortestPath(layer, 1, 4);
        Assert.Single(path);
        Assert.Equal(1, path[0].Start);
        Assert.Equal(4, path[0].Count);
    }

    [Fact]
    public void ClosedLoop_WrapsWhenShorter()
    {
        var layer = ClosedLoop(20);
        // 18 → 1: forward 18→19→0→1 = 3 steps (4 points); reverse is long.
        var path = ContourPointPath.ShortestPath(layer, 18, 1);
        Assert.Equal(2, path.Count);
        Assert.Equal(18, path[0].Start);
        Assert.Equal(2, path[0].Count); // 18,19
        Assert.Equal(0, path[1].Start);
        Assert.Equal(2, path[1].Count); // 0,1
        Assert.Equal(4, TotalMoves(path));
    }

    [Fact]
    public void ClosedLoop_BackwardShortArc_NoWrap()
    {
        var layer = ClosedLoop(20);
        // 5 → 2 backward is short (4 points: 2,3,4,5)
        var path = ContourPointPath.ShortestPath(layer, 5, 2);
        Assert.Single(path);
        Assert.Equal(2, path[0].Start);
        Assert.Equal(4, path[0].Count);
    }

    [Fact]
    public void DifferentContours_ReturnsEmpty()
    {
        var layer = new ToolpathLayer(0, 0f);
        for (int i = 0; i < 10; i++)
            layer.Moves.Add(new ToolpathMove(
                new Vector3(i, 0, 0), new Vector3(i + 1, 0, 0), MoveKind.Extrude));
        // Gap, then second island
        layer.Moves.Add(new ToolpathMove(
            new Vector3(100, 0, 0), new Vector3(110, 0, 0), MoveKind.Travel));
        for (int i = 0; i < 5; i++)
            layer.Moves.Add(new ToolpathMove(
                new Vector3(200 + i, 0, 0), new Vector3(201 + i, 0, 0), MoveKind.Extrude));
        layer.Contours.Add(new ContourSpan(0, 10, false, -1));
        layer.Contours.Add(new ContourSpan(11, 5, false, -1));

        var path = ContourPointPath.ShortestPath(layer, 2, 12);
        Assert.Empty(path);
    }

    [Fact]
    public void SamePoint_SingleBead()
    {
        var layer = OpenLine(5);
        var path = ContourPointPath.ShortestPath(layer, 2, 2);
        Assert.Single(path);
        Assert.Equal(1, path[0].Count);
        Assert.Equal(2, path[0].Start);
    }
}
