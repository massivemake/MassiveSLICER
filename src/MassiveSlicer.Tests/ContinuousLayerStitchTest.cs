using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// A layer's beads join into one continuous extrusion where a short bridge
/// reaches the nearest end or the nearest line. A bridge does not cross or
/// ride a bead, and a bead is not printed twice.
/// </summary>
public sealed class ContinuousLayerStitchTest
{
    static Toolpath LayerOf(params (Vector3 A, Vector3 B)[] segs)
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, segs[0].A.Z);
        foreach (var (a, b) in segs)
            layer.Moves.Add(new ToolpathMove(a, b, MoveKind.Extrude));
        tp.Layers.Add(layer);
        return tp;
    }

    static List<ToolpathMove> Extrude(Toolpath tp) =>
        tp.Layers[0].Moves.Where(m => m.Kind == MoveKind.Extrude).ToList();

    [Fact]
    public void GapBetweenEnds_BecomesOneBridge()
    {
        var tp = LayerOf(
            (new(0, 0, 1), new(10, 0, 1)),
            (new(12, 0, 1), new(22, 0, 1)));

        var stats = ContinuousLayerStitch.Apply(tp, beadWidth: 4f);

        Assert.Equal(1, stats.PiecesAfter);
        Assert.Equal(0, tp.Layers[0].Moves.Count(m => m.Kind == MoveKind.Travel));
        var extrude = Extrude(tp);
        Assert.Contains(extrude, m => MathF.Abs(m.From.X - 10f) < 0.1f && MathF.Abs(m.To.X - 12f) < 0.1f
            || MathF.Abs(m.From.X - 12f) < 0.1f && MathF.Abs(m.To.X - 10f) < 0.1f);
        AssertClean(extrude);
    }

    [Fact]
    public void Infill_BridgesOntoTheWall_AsOnePath()
    {
        var tp = LayerOf(
            (new(0, 0, 2), new(40, 0, 2)),
            (new(40, 0, 2), new(40, 40, 2)),
            (new(40, 40, 2), new(0, 40, 2)),
            (new(0, 40, 2), new(0, 0, 2)),
            (new(2, 20, 2), new(38, 20, 2)));

        var stats = ContinuousLayerStitch.Apply(tp, beadWidth: 4f);

        Assert.Equal(1, stats.PiecesAfter);
        Assert.True(stats.Bridges >= 1);
        Assert.DoesNotContain(tp.Layers[0].Moves, m => m.Kind == MoveKind.Travel);
        var extrude = Extrude(tp);
        Assert.Contains(extrude, m => Near(m, new Vector2(20, 20)));
        Assert.Contains(extrude, m => Near(m, new Vector2(0, 0)) || Near(m, new Vector2(40, 0)));
        AssertClean(extrude);
        AssertConnected(extrude);
    }

    [Fact]
    public void IslandsFartherThanTheBridge_StaySeparate()
    {
        var tp = LayerOf(
            (new(0, 0, 1), new(10, 0, 1)),
            (new(40, 0, 1), new(50, 0, 1)));

        ContinuousLayerStitch.Apply(tp, beadWidth: 4f);

        Assert.Contains(tp.Layers[0].Moves, m => m.Kind == MoveKind.Travel);
        foreach (var move in Extrude(tp))
            Assert.True(Vector3.Distance(move.From, move.To) < 15f);
        AssertClean(Extrude(tp));
    }

    [Fact]
    public void BridgeThatWouldRideABead_IsLeftOut()
    {
        var tp = LayerOf(
            (new(0, 0, 1), new(8, 0, 1)),
            (new(0, 10, 1), new(8, 10, 1)),
            (new(8, 1, 1), new(8, 9, 1)));

        ContinuousLayerStitch.Apply(tp, beadWidth: 4f);

        Assert.DoesNotContain(Extrude(tp), m =>
            MathF.Abs(m.From.X - 8f) < 0.1f && MathF.Abs(m.To.X - 8f) < 0.1f
            && MathF.Min(m.From.Y, m.To.Y) < 0.1f && MathF.Max(m.From.Y, m.To.Y) > 9.9f);
        AssertClean(Extrude(tp));
    }

    static bool Near(ToolpathMove move, Vector2 p)
    {
        var a = new Vector2(move.From.X, move.From.Y);
        var b = new Vector2(move.To.X, move.To.Y);
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-8f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t) < 0.6f;
    }

    static void AssertConnected(List<ToolpathMove> extrude)
    {
        for (int i = 1; i < extrude.Count; i++)
            Assert.True(Vector3.Distance(extrude[i - 1].To, extrude[i].From) < 0.1f);
    }

    static void AssertClean(List<ToolpathMove> extrude)
    {
        for (int i = 0; i < extrude.Count; i++)
        for (int j = i + 1; j < extrude.Count; j++)
        {
            var a = new Vector2(extrude[i].From.X, extrude[i].From.Y);
            var b = new Vector2(extrude[i].To.X, extrude[i].To.Y);
            var c = new Vector2(extrude[j].From.X, extrude[j].From.Y);
            var d = new Vector2(extrude[j].To.X, extrude[j].To.Y);
            Assert.False(ContinuousPlanarField.OverlapsBead(a, b, c, d));
            Assert.False(ContinuousPlanarField.ProperCross(a, b, c, d));
        }
    }
}
