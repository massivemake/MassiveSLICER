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
    public void Inside_the_window_the_angle_falls_off_to_the_baseline_at_the_tail()
    {
        var key = Key(100, y: -90f, left: 100, right: 100);
        Assert.Equal(0f, At(0, baseY: 0f, key).B, 3);
        Assert.Equal(-45f, At(50, baseY: 0f, key).B, 3);
        Assert.Equal(-90f, At(100, baseY: 0f, key).B, 3);
    }

    [Fact]
    public void Past_the_tail_the_angle_stays_on_the_baseline()
    {
        var key = Key(100, y: 90f, left: 40, right: 40);
        var past = ToolheadOrientEase.At(150, new ToolheadOrientEase.Degrees(0f, 12f, 0f), new[] { key }, 200);
        Assert.Equal(12f, past.B, 3);
        Assert.Equal(12f, At(50, baseY: 12f, key).B, 3);
    }

    [Fact]
    public void A_blank_gap_between_windows_stays_on_the_baseline()
    {
        var keys = new[] { Key(100, y: 94f), Key(200, y: 10f) };
        Assert.Equal(0f, At(150, baseY: 0f, keys).B, 3);
        Assert.Equal(12f, At(150, baseY: 12f, keys).B, 3);
    }

    [Fact]
    public void A_keys_window_falls_off_on_its_own_when_another_key_is_elsewhere()
    {
        var keys = new[] { Key(100, y: 94f, right: 20), Key(200, y: 10f, left: 20) };
        // Move 110 is halfway along the first tail and outside the second window.
        Assert.Equal(47f, At(110, baseY: 0f, keys).B, 3);
        Assert.Equal(5f, At(190, baseY: 0f, keys).B, 3);
        Assert.Equal(0f, At(150, baseY: 0f, keys).B, 3);
    }

    [Fact]
    public void The_other_keys_diamond_stays_put_when_windows_overlap()
    {
        var keys = new[] { Key(100, y: 94f, right: 80), Key(140, y: 10f, left: 80) };
        Assert.Equal(10f, At(140, baseY: 0f, keys).B, 3);
        Assert.Equal(94f, At(100, baseY: 0f, keys).B, 3);
    }

    [Fact]
    public void Overlapping_windows_blend_and_a_gap_does_not()
    {
        var overlap = new[] { Key(100, y: 94f, right: 80), Key(140, y: 10f, left: 80) };
        Assert.Equal(52f, At(120, baseY: 12f, overlap).B, 3);

        var gap = new[] { Key(0, y: -90f, right: 10), Key(100, y: 90f, left: 10) };
        Assert.Equal(12f, At(50, baseY: 12f, gap).B, 3);
    }

    [Fact]
    public void Z_half_turn_from_negative_to_positive_passes_through_zero()
    {
        // Z is KUKA A. Exact −90 to +90 already crosses 0. A fraction past a half-turn
        // used to wrap across ±180, and the slider then sat on −180 until the next key.
        var exact = new[]
        {
            new ToolheadOrientEase.Key(0, A: -90f, B: 0f, C: 0f, 1, 100),
            new ToolheadOrientEase.Key(100, A: 90f, B: 0f, C: 0f, 100, 1),
        };
        Assert.Equal(0f, ToolheadOrientEase.At(50, new ToolheadOrientEase.Degrees(0, 0, 0), exact).A, 3);

        Assert.Equal(181f, ToolheadOrientEase.ShortestDelta(-90.5f, 90.5f), 3);
        var hair = new[]
        {
            new ToolheadOrientEase.Key(0, A: -90.5f, B: 0f, C: 0f, 1, 100),
            new ToolheadOrientEase.Key(100, A: 90.5f, B: 0f, C: 0f, 100, 1),
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
            new ToolheadOrientEase.Key(0, A: 170f, B: 0f, C: 0f, 1, 10),
            new ToolheadOrientEase.Key(10, A: -170f, B: 0f, C: 0f, 10, 1),
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
