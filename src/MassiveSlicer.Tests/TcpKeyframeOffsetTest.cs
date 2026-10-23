using System.Numerics;
using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.Tests;

public class TcpKeyframeOffsetTest
{
    static TcpKeyframeOffset.Sample Key(int index, float x, int left = 40, int right = 40)
        => new(index, new Vector3(x, 0f, 0f), left, right);

    [Fact]
    public void On_a_key_the_offset_is_that_keys_even_when_windows_overlap()
    {
        var keys = new[] { Key(100, x: 20f), Key(120, x: 4f) };
        var atSecond = TcpKeyframeOffset.At(120, keys);
        Assert.Equal(4f, atSecond.X, 3);
        var atFirst = TcpKeyframeOffset.At(100, keys);
        Assert.Equal(20f, atFirst.X, 3);
    }

    [Fact]
    public void Outside_every_window_the_offset_is_zero()
    {
        var keys = new[] { Key(100, x: 20f, right: 10) };
        var far = TcpKeyframeOffset.At(130, keys);
        Assert.Equal(0f, far.X, 3);
    }

    [Fact]
    public void A_point_in_one_window_does_not_pick_up_the_other_key()
    {
        var keys = new[] { Key(100, x: 20f, right: 10), Key(200, x: 4f, left: 10) };
        var held = TcpKeyframeOffset.At(108, keys);
        var onlyFirst = TcpKeyframeOffset.At(108, new[] { keys[0] });
        Assert.Equal(onlyFirst.X, held.X, 3);
        Assert.True(held.X > 0f);
    }
}
