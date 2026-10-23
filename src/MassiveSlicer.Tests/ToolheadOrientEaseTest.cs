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
    public void Before_the_first_key_eases_from_the_start_of_the_path()
    {
        // A one-move tail used to stay at the baseline until the diamond.
        var key = Key(100, y: -90f, left: 1, right: 1);
        Assert.Equal(0f, At(0, baseY: 0f, key).B, 3);
        Assert.Equal(-45f, At(50, baseY: 0f, key).B, 3);
        Assert.Equal(-90f, At(100, baseY: 0f, key).B, 3);
    }

    [Fact]
    public void After_the_last_key_eases_out_to_the_end_of_the_path()
    {
        var key = Key(100, y: 90f, left: 1, right: 1);
        var mid = ToolheadOrientEase.At(150, new ToolheadOrientEase.Degrees(0f, 0f, 0f), new[] { key }, 200);
        var end = ToolheadOrientEase.At(200, new ToolheadOrientEase.Degrees(0f, 0f, 0f), new[] { key }, 200);
        Assert.Equal(45f, mid.B, 3);
        Assert.Equal(0f, end.B, 3);
    }

    [Fact]
    public void Between_two_keys_eases_from_one_saved_orientation_to_the_next()
    {
        var keys = new[] { Key(100, y: 94f), Key(200, y: 10f) };
        var mid = At(150, baseY: 0f, keys);
        Assert.Equal(52f, mid.B, 3);

        // The blank gap does not fall back to the non-keyed 0° while both keys are in play.
        Assert.True(mid.B > 40f);
    }

    [Fact]
    public void A_keys_window_keeps_its_own_angle_when_another_key_changes()
    {
        var keys = new[] { Key(100, y: 94f, right: 20), Key(200, y: 10f, left: 20) };
        // Move 110 is inside the first window (ends at 120) and outside the second (starts at 180).
        var held = At(110, baseY: 0f, keys);
        Assert.Equal(94f, held.B, 3);

        var other = At(190, baseY: 0f, keys);
        Assert.Equal(10f, other.B, 3);
    }

    [Fact]
    public void The_other_keys_diamond_stays_put_when_windows_overlap()
    {
        var keys = new[] { Key(100, y: 94f, right: 80), Key(140, y: 10f, left: 80) };
        Assert.Equal(10f, At(140, baseY: 0f, keys).B, 3);
        Assert.Equal(94f, At(100, baseY: 0f, keys).B, 3);
    }

    [Fact]
    public void Opposite_angles_blend_to_zero_halfway_between_the_keys()
    {
        var keys = new[] { Key(0, y: -90f, right: 10), Key(100, y: 90f, left: 10) };
        Assert.Equal(0f, At(50, baseY: 0f, keys).B, 3);
    }

    [Fact]
    public void Z_half_turn_from_negative_to_positive_passes_through_zero()
    {
        // Z is KUKA A. Exact −90 to +90 already crosses 0. A fraction past a half-turn
        // used to wrap across ±180, and the slider then sat on −180 until the next key.
        var exact = new[]
        {
            new ToolheadOrientEase.Key(0, A: -90f, B: 0f, C: 0f, 1, 1),
            new ToolheadOrientEase.Key(100, A: 90f, B: 0f, C: 0f, 1, 1),
        };
        Assert.Equal(0f, ToolheadOrientEase.At(50, new ToolheadOrientEase.Degrees(0, 0, 0), exact).A, 3);

        Assert.Equal(181f, ToolheadOrientEase.ShortestDelta(-90.5f, 90.5f), 3);
        var hair = new[]
        {
            new ToolheadOrientEase.Key(0, A: -90.5f, B: 0f, C: 0f, 1, 1),
            new ToolheadOrientEase.Key(100, A: 90.5f, B: 0f, C: 0f, 1, 1),
        };
        var origin = new ToolheadOrientEase.Degrees(0, 0, 0);
        Assert.Equal(0f, ToolheadOrientEase.At(50, origin, hair).A, 3);
        Assert.True(ToolheadOrientEase.At(90, origin, hair).A > 50f);
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
