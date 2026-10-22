using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public class ToolheadOrientEaseTest
{
    static ToolheadOrientEase.Key Key(int index, float y, int left = 10, int right = 10)
        => new(index, A: 0f, B: y, C: 0f, left, right);

    static ToolheadOrientEase.Degrees At(double i, float baseY, params ToolheadOrientEase.Key[] keys)
        => ToolheadOrientEase.At(i, new ToolheadOrientEase.Degrees(0f, baseY, 0f), keys);

    [Fact]
    public void No_keys_holds_the_nonkeyed_orientation()
    {
        var o = At(40, baseY: 12f);
        Assert.Equal(12f, o.B);
    }

    [Fact]
    public void On_the_key_the_saved_orientation_is_used()
    {
        var o = At(100, baseY: 0f, Key(100, y: 94f));
        Assert.Equal(94f, o.B, 3);
    }

    [Fact]
    public void Past_the_influence_window_eases_back_to_the_nonkeyed_orientation()
    {
        var o = At(130, baseY: 0f, Key(100, y: 94f, right: 10));
        Assert.Equal(0f, o.B, 3);
    }

    [Fact]
    public void Halfway_out_of_a_lone_key_is_halfway_to_the_nonkeyed_orientation()
    {
        // smoothstep(0.5) = 0.5, so the midpoint of the window is a straight blend.
        var o = At(105, baseY: 0f, Key(100, y: 94f, right: 10));
        Assert.Equal(47f, o.B, 3);
    }

    [Fact]
    public void Between_two_keys_eases_from_one_saved_orientation_to_the_next()
    {
        var keys = new[] { Key(100, y: 94f), Key(200, y: 10f) };
        var mid = At(150, baseY: 0f, keys);
        Assert.Equal(52f, mid.B, 3);

        // The gap does not fall back to the non-keyed 0° while both keys are in play.
        Assert.True(mid.B > 40f);
    }

    [Fact]
    public void Shortest_turn_crosses_180_instead_of_unwinding_through_zero()
    {
        float d = ToolheadOrientEase.ShortestDelta(170f, -170f);
        Assert.Equal(20f, d, 3);
        var keys = new[]
        {
            new ToolheadOrientEase.Key(0, A: 170f, B: 0f, C: 0f, 1, 1),
            new ToolheadOrientEase.Key(10, A: -170f, B: 0f, C: 0f, 1, 1),
        };
        var mid = ToolheadOrientEase.At(5, new ToolheadOrientEase.Degrees(0, 0, 0), keys);
        Assert.Equal(180f, mid.A, 3);
    }

    [Fact]
    public void Clone_keeps_a_keyframed_toolhead_orientation()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0f);
        layer.Moves.Add(new ToolpathMove(System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX, MoveKind.Mill)
        {
            KeyframeToolheadB = 94f,
        });
        tp.Layers.Add(layer);

        var copy = ToolpathClone.Copy(tp);
        Assert.Equal(94f, copy.Layers[0].Moves[0].KeyframeToolheadB);
    }
}
