using MassiveSlicer.App;
using Xunit;

namespace MassiveSlicer.Tests;

public class TcpKeyframeIndexTest
{
    [Fact]
    public void A_free_index_is_kept()
    {
        Assert.Equal(40, TcpKeyframeIndex.Place(40, 10, 100, [10, 80]));
    }

    [Fact]
    public void An_occupied_index_steps_in_the_drag_direction()
    {
        Assert.Equal(81, TcpKeyframeIndex.Place(80, 10, 100, [80]));
        Assert.Equal(79, TcpKeyframeIndex.Place(80, 90, 100, [80]));
    }

    [Fact]
    public void The_index_stays_inside_the_timeline()
    {
        Assert.Equal(0, TcpKeyframeIndex.Place(-5, 3, 20, []));
        Assert.Equal(20, TcpKeyframeIndex.Place(40, 3, 20, []));
    }
}
