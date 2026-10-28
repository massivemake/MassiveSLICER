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

    [Fact]
    public void Apply_perpendicular_wipe_turns_toward_the_closer_next_bead()
    {
        // Bead along +X. The next bead is 12 mm in +Y, and there is no previous bead.
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(Extrude(0, 0, 50, 0));
        layer.Moves.Add(Travel(50, 0, 50, 12));
        layer.Moves.Add(Extrude(50, 12, 100, 12));

        var wipe = OnlyWipe(layer, 20f);

        Assert.Equal(50f, wipe.To.X, 0.01f);
        Assert.True(wipe.To.Y > wipe.From.Y, "wipe should step toward the next bead");
        Assert.Equal(20f, Vector3.Distance(wipe.From, wipe.To), 0.01f);
        Assert.Equal(0f, Vector3.Dot(Vector3.Normalize(wipe.To - wipe.From), Vector3.UnitX), 0.01f);
    }

    [Fact]
    public void Apply_perpendicular_wipe_prefers_the_closer_previous_bead()
    {
        // Previous bead is 8 mm below. Next bead is 40 mm above. Wipe goes toward the previous one.
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(Extrude(0, -8, 50, -8));
        layer.Moves.Add(Extrude(0, 0, 50, 0));
        layer.Moves.Add(Travel(50, 0, 50, 40));
        layer.Moves.Add(Extrude(50, 40, 100, 40));

        var wipe = OnlyWipe(layer, 15f);

        Assert.True(wipe.To.Y < wipe.From.Y, "wipe should step toward the closer previous bead");
        Assert.Equal(50f, wipe.To.X, 0.01f);
    }

    [Fact]
    public void Apply_perpendicular_wipe_uses_travel_when_no_neighbor_is_beside_the_bead()
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(Extrude(0, 0, 50, 0));
        layer.Moves.Add(Travel(50, 0, 50, 30));

        var wipe = OnlyWipe(layer, 10f);

        Assert.True(wipe.To.Y > wipe.From.Y);
        Assert.Equal(50f, wipe.To.X, 0.01f);
    }

    [Fact]
    public void Apply_perpendicular_wipe_stays_in_the_layer_plane()
    {
        // Plane normal +Y, so the layer is the XZ plane. Neighbor sits in +Z.
        var layer = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitY };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 0), new Vector3(50, 0, 0), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 0), new Vector3(50, 0, 12), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 12), new Vector3(100, 0, 12), MoveKind.Extrude));

        var wipe = OnlyWipe(layer, 10f);
        var dir = Vector3.Normalize(wipe.To - wipe.From);

        Assert.Equal(0f, dir.Y, 0.01f);
        Assert.True(dir.Z > 0.9f);
    }

    [Fact]
    public void Apply_same_direction_turns_back_when_forward_would_cross_the_bed()
    {
        // Bead descends toward the bed. Continuing forward for 40 mm goes below Z = 0.
        var layer = new ToolpathLayer(0, 0.5f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 2), new Vector3(30, 0, 0.5f), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(30, 0, 0.5f), new Vector3(30, 20, 0.5f), MoveKind.Travel));

        var wipe = WipeOf(layer, WipeMode.SameDirection, 40f, 0f);

        Assert.True(wipe.To.X < wipe.From.X, "forward crosses the bed, so the wipe retraces up the bead");
        Assert.True(wipe.To.Z >= 0f);
        Assert.True(wipe.From.Z >= 0f);
    }

    [Fact]
    public void Apply_retrace_continues_forward_when_back_would_cross_the_bed()
    {
        var layer = new ToolpathLayer(0, 4f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 0.4f), new Vector3(30, 0, 4f), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(30, 0, 4f), new Vector3(30, 20, 4f), MoveKind.Travel));

        var wipe = WipeOf(layer, WipeMode.Retrace, 40f, 0f);

        Assert.True(wipe.To.X > wipe.From.X, "retrace crosses the bed, so the wipe keeps going up the bead");
        Assert.True(wipe.To.Z >= 0f);
    }

    [Fact]
    public void Apply_perpendicular_picks_the_other_side_when_the_neighbor_side_crosses_the_bed()
    {
        // +side is (0, +0.8, -0.6): toward the next bead and down through Z = 0.
        var n = new Vector3(0f, 0.6f, 0.8f);
        var side = Vector3.Normalize(Vector3.Cross(n, Vector3.UnitX));
        var end = new Vector3(40, 0, 1);
        var next = end + side * 10f;

        var layer = new ToolpathLayer(0, 1f) { PlaneNormal = n };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 1), end, MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(end, next, MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(next, next + new Vector3(40, 0, 0), MoveKind.Extrude));

        var wipe = WipeOf(layer, WipeMode.Perpendicular, 20f, 0f);

        Assert.True(wipe.To.Y < wipe.From.Y, "the downward side is rejected for the upward side");
        Assert.True(wipe.To.Z >= 0f);
        Assert.True(wipe.From.Z >= 0f);
    }

    [Fact]
    public void Apply_smash_stops_at_the_bed()
    {
        var layer = new ToolpathLayer(0, 0.4f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 0.4f), new Vector3(40, 0, 0.4f), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(40, 0, 0.4f), new Vector3(40, 20, 0.4f), MoveKind.Travel));

        var tp = new Toolpath();
        tp.Layers.Add(layer);
        var moves = MovementPostProcessor.Apply(tp, new SliceSettings
        {
            LayerHeight  = 3f,
            WipeMode     = WipeMode.SameDirection,
            WipeLengthMm = 15f,
            WipeRampMm   = -1f,
        }).Layers[0].Moves;

        var wipes = moves.Where(m => m.IsWipe).ToList();
        Assert.Equal(2, wipes.Count);
        Assert.Equal(0f, wipes[0].WipeRpmScale);
        Assert.Equal(0f, wipes[0].To.Z, 0.01f);
        Assert.All(wipes, m =>
        {
            Assert.True(m.From.Z >= -0.001f);
            Assert.True(m.To.Z >= -0.001f);
        });
    }

    [Fact]
    public void Apply_same_direction_respects_a_raised_bed()
    {
        // LFAM 2 bed is at Z = 130. Continuing this bead for 40 mm finishes near 116.
        var layer = new ToolpathLayer(0, 132f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 150), new Vector3(40, 0, 132), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(40, 0, 132), new Vector3(40, 20, 132), MoveKind.Travel));

        var tp = new Toolpath();
        tp.Layers.Add(layer);
        var wipe = Assert.Single(MovementPostProcessor.Apply(tp, new SliceSettings
        {
            WipeMode     = WipeMode.SameDirection,
            WipeLengthMm = 40f,
            WipeRampMm   = 0f,
            BedZ         = 130f,
        }).Layers[0].Moves.Where(m => m.IsWipe));

        Assert.True(wipe.To.X < wipe.From.X);
        Assert.True(wipe.To.Z >= 130f);
        Assert.True(wipe.From.Z >= 130f);
    }

    [Fact]
    public void End_retract_lifts_straight_up_from_the_last_point()
    {
        var layer = new ToolpathLayer(0, 12f) { PlaneNormal = new Vector3(0f, 0.6f, 0.8f) };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(8, 3, 14), MoveKind.Extrude)
            { Normal = new Vector3(0f, 0.6f, 0.8f) });
        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var off = MovementPostProcessor.Apply(tp, new SliceSettings { EndRetractDistanceMm = 40f });
        Assert.Same(tp, off);

        var on = MovementPostProcessor.Apply(tp, new SliceSettings
        {
            EndRetractEnabled = true,
            EndRetractDistanceMm = 40f,
        });
        var lift = on.Layers[0].Moves[^1];
        Assert.Equal(MoveKind.Travel, lift.Kind);
        Assert.True(lift.IsZHop);
        Assert.Equal(8f, lift.From.X, 0.01f);
        Assert.Equal(3f, lift.From.Y, 0.01f);
        Assert.Equal(14f, lift.From.Z, 0.01f);
        Assert.Equal(8f, lift.To.X, 0.01f);
        Assert.Equal(3f, lift.To.Y, 0.01f);
        Assert.Equal(54f, lift.To.Z, 0.01f);
    }

    private static ToolpathMove Extrude(float x0, float y0, float x1, float y1) =>
        new(new Vector3(x0, y0, 10), new Vector3(x1, y1, 10), MoveKind.Extrude) { Normal = Vector3.UnitZ };

    private static ToolpathMove Travel(float x0, float y0, float x1, float y1) =>
        new(new Vector3(x0, y0, 10), new Vector3(x1, y1, 10), MoveKind.Travel);

    private static ToolpathMove OnlyWipe(ToolpathLayer layer, float lengthMm) =>
        WipeOf(layer, WipeMode.Perpendicular, lengthMm, 0f);

    private static ToolpathMove WipeOf(ToolpathLayer layer, WipeMode mode, float lengthMm, float rampMm)
    {
        var tp = new Toolpath();
        tp.Layers.Add(layer);
        var result = MovementPostProcessor.Apply(tp, new SliceSettings
        {
            WipeMode     = mode,
            WipeLengthMm = lengthMm,
            WipeRampMm   = rampMm,
        });
        return Assert.Single(result.Layers[0].Moves.Where(m => m.IsWipe));
    }
}