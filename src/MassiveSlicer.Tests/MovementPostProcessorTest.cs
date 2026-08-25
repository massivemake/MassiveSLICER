using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

public class MovementPostProcessorTest
{
    [Fact]
    public void Apply_inserts_wipe_and_z_hop_before_travel()
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));

        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var settings = new SliceSettings
        {
            ZHopMm       = 5f,
            WipeMode     = WipeMode.Retrace,
            WipeLengthMm = 8f,
            WipeRampMm   = 4f,
        };

        var result = MovementPostProcessor.Apply(tp, settings);
        var moves  = result.Layers[0].Moves;

        Assert.True(moves.Exists(m => m.IsWipe));
        Assert.Equal(3, moves.Count(m => m.IsZHop));
        Assert.True(moves.FindIndex(m => m.IsWipe) < moves.FindIndex(m => m.IsZHop));
    }

    [Fact]
    public void Apply_same_direction_negative_ramp_smashes_z_then_wipes()
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));

        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var settings = new SliceSettings
        {
            LayerHeight  = 3f,
            WipeMode     = WipeMode.SameDirection,
            WipeLengthMm = 35f,
            WipeRampMm   = -1f,
        };

        var result = MovementPostProcessor.Apply(tp, settings);
        var wipes  = result.Layers[0].Moves.Where(m => m.IsWipe).ToList();

        Assert.Equal(2, wipes.Count);
        Assert.Equal(0f, wipes[0].WipeRpmScale);
        Assert.Equal(50f, wipes[0].From.X, 0.01f);
        Assert.Equal(50f, wipes[0].To.X, 0.01f);
        Assert.Equal(10f, wipes[0].From.Z, 0.01f);
        Assert.Equal(9f, wipes[0].To.Z, 0.01f);
        Assert.True(wipes[1].WipeRpmScale >= 0.99f);
        Assert.Equal(35f, Vector3.Distance(wipes[1].From, wipes[1].To), 0.1f);
        Assert.Equal(9f, wipes[1].From.Z, 0.01f);
        Assert.Equal(9f, wipes[1].To.Z, 0.01f);
    }

    [Fact]
    public void Apply_negative_ramp_smash_capped_at_layer_height()
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));
        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var result = MovementPostProcessor.Apply(tp, new SliceSettings
        {
            LayerHeight  = 3f,
            WipeMode     = WipeMode.SameDirection,
            WipeLengthMm = 10f,
            WipeRampMm   = -20f,
        });
        var smash = result.Layers[0].Moves.First(m => m.IsWipe);
        Assert.Equal(0f, smash.WipeRpmScale);
        Assert.Equal(7f, smash.To.Z, 0.01f);
    }

    [Fact]
    public void Apply_z_hop_lifts_from_wipe_endpoint_not_travel_start()
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));

        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var settings = new SliceSettings
        {
            ZHopMm       = 5f,
            WipeMode     = WipeMode.SameDirection,
            WipeLengthMm = 35f,
            WipeRampMm   = -1f,
        };

        var result = MovementPostProcessor.Apply(tp, settings);
        var moves  = result.Layers[0].Moves;
        var wipes  = moves.Where(m => m.IsWipe).ToList();
        var zHops  = moves.Where(m => m.IsZHop).ToList();

        var wipeEnd = wipes[^1].To;
        var liftUp  = zHops[0];

        Assert.Equal(wipeEnd, liftUp.From);
        Assert.Equal(wipeEnd.X, liftUp.To.X, 0.01f);
        Assert.Equal(wipeEnd.Y, liftUp.To.Y, 0.01f);
        Assert.Equal(wipeEnd.Z + 5f, liftUp.To.Z, 0.01f);
    }

    [Fact]
    public void Apply_skips_wipe_on_short_travel_when_enabled()
    {
        // Layer height 3 mm → short-travel threshold = 6 mm.
        var layer = new ToolpathLayer(0, 10f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        // 4 mm travel — under 2× layer height.
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(54, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(54, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        // 20 mm travel — over threshold, still gets a wipe.
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(120, 0, 10), MoveKind.Travel));

        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var settings = new SliceSettings
        {
            LayerHeight          = 3f,
            WipeMode             = WipeMode.Retrace,
            WipeLengthMm         = 8f,
            WipeRampMm           = 0f,
            WipeSkipShortTravels = true,
        };

        var result = MovementPostProcessor.Apply(tp, settings);
        var moves  = result.Layers[0].Moves;
        int wipeCount = moves.Count(m => m.IsWipe);

        // Only the long travel should get a wipe (one full-length segment).
        Assert.Equal(1, wipeCount);
        // Wipe appears after the second extrude, before the long travel.
        int longTravelIdx = moves.FindIndex(m =>
            m.Kind == MoveKind.Travel && Vector3.Distance(m.From, m.To) > 15f);
        int wipeIdx = moves.FindIndex(m => m.IsWipe);
        Assert.True(wipeIdx >= 0 && wipeIdx < longTravelIdx);
    }

    [Fact]
    public void Apply_short_travel_still_wipes_when_skip_disabled()
    {
        var layer = new ToolpathLayer(0, 10f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(54, 0, 10), MoveKind.Travel));

        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var settings = new SliceSettings
        {
            LayerHeight          = 3f,
            WipeMode             = WipeMode.Retrace,
            WipeLengthMm         = 8f,
            WipeRampMm           = 0f,
            WipeSkipShortTravels = false,
        };

        var result = MovementPostProcessor.Apply(tp, settings);
        Assert.True(result.Layers[0].Moves.Exists(m => m.IsWipe));
    }

    [Fact]
    public void Apply_wipes_on_layer_change_travel_using_previous_bead()
    {
        var l0 = new ToolpathLayer(0, 5f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        l0.Moves.Add(new ToolpathMove(new Vector3(0, 0, 5), new Vector3(50, 0, 5), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        var l1 = new ToolpathLayer(1, 8f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        l1.Moves.Add(new ToolpathMove(new Vector3(50, 0, 5), new Vector3(80, 10, 8), MoveKind.Travel)
            { IsLayerChange = true });
        l1.Moves.Add(new ToolpathMove(new Vector3(80, 10, 8), new Vector3(120, 10, 8), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });

        var tp = new Toolpath();
        tp.Layers.Add(l0);
        tp.Layers.Add(l1);

        var result = MovementPostProcessor.Apply(tp, new SliceSettings
        {
            LayerHeight  = 3f,
            WipeMode     = WipeMode.SameDirection,
            WipeLengthMm = 35f,
            WipeRampMm   = -1f,
        });

        var wipes = result.Layers[1].Moves.Where(m => m.IsWipe).ToList();
        Assert.True(wipes.Count >= 2, "layer-change travel must smash then wipe");
        Assert.Equal(0f, wipes[0].WipeRpmScale);
        Assert.Equal(50f, wipes[0].From.X, 0.01f);
        Assert.Equal(5f, wipes[0].From.Z, 0.01f);
        Assert.Equal(4f, wipes[0].To.Z, 0.01f);
        Assert.True(result.Layers[1].Moves.FindIndex(m => m.IsWipe)
                    < result.Layers[1].Moves.FindIndex(m => m.Kind == MoveKind.Travel));
    }
}