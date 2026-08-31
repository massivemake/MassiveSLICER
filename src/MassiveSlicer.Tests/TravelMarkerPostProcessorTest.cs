using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

public sealed class TravelMarkerPostProcessorTest
{
    [Fact]
    public void Apply_splits_100mm_before_wipe_and_after_travel()
    {
        var tp = HopPath(printMm: 200f, wipeMm: 10f, travelMm: 40f, resumeMm: 200f);
        var result = TravelMarkerPostProcessor.Apply(tp);
        var moves = result.Layers[0].Moves;

        var pre = moves.Single(m => m.IsPreTravelStart);
        Assert.False(pre.IsWipe);
        Assert.Equal(100f, pre.From.X, 0.05f);
        Assert.Equal(200f, pre.To.X, 0.05f);

        var wipe = moves.First(m => m.IsWipe);
        Assert.Equal(200f, wipe.From.X, 0.05f);

        var post = moves.Single(m => m.IsPostTravelEnd);
        Assert.False(post.IsWipe);
        Assert.Equal(250f, post.From.X, 0.05f);
        Assert.Equal(350f, post.To.X, 0.05f);

        Assert.Same(result, TravelMarkerPostProcessor.Apply(result));
    }

    [Fact]
    public void Apply_travel_without_wipe_still_marks_100mm()
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(200, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(200, 0, 10), new Vector3(250, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(250, 0, 10), new Vector3(450, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var moves = TravelMarkerPostProcessor.Apply(tp).Layers[0].Moves;
        var pre = moves.Single(m => m.IsPreTravelStart);
        Assert.Equal(100f, pre.From.X, 0.05f);
        var post = moves.Single(m => m.IsPostTravelEnd);
        Assert.Equal(350f, post.To.X, 0.05f);
    }

    [Fact]
    public void Apply_short_bead_clamps_to_available_print()
    {
        var tp = HopPath(printMm: 40f, wipeMm: 8f, travelMm: 20f, resumeMm: 30f);
        var moves = TravelMarkerPostProcessor.Apply(tp).Layers[0].Moves;
        var pre = moves.Single(m => m.IsPreTravelStart);
        Assert.Equal(0f, pre.From.X, 0.05f);
        Assert.Equal(40f, pre.To.X, 0.05f);
        var post = moves.Single(m => m.IsPostTravelEnd);
        Assert.Equal(30f, Vector3.Distance(post.From, post.To), 0.05f);
    }

    [Fact]
    public void Apply_no_hop_leaves_path_alone()
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(200, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        var tp = new Toolpath();
        tp.Layers.Add(layer);
        var result = TravelMarkerPostProcessor.Apply(tp);
        Assert.False(TravelMarkerPostProcessor.HasMarkers(result));
        Assert.Single(result.Layers[0].Moves);
    }

    static Toolpath HopPath(float printMm, float wipeMm, float travelMm, float resumeMm)
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        float x = 0f;
        layer.Moves.Add(new ToolpathMove(new Vector3(x, 0, 10), new Vector3(x + printMm, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        x += printMm;
        layer.Moves.Add(new ToolpathMove(new Vector3(x, 0, 10), new Vector3(x + wipeMm, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ, IsWipe = true });
        x += wipeMm;
        layer.Moves.Add(new ToolpathMove(new Vector3(x, 0, 10), new Vector3(x + travelMm, 0, 10), MoveKind.Travel));
        x += travelMm;
        layer.Moves.Add(new ToolpathMove(new Vector3(x, 0, 10), new Vector3(x + resumeMm, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        var tp = new Toolpath();
        tp.Layers.Add(layer);
        return tp;
    }
}
