using MassiveSlicer.App;
using Xunit;

namespace MassiveSlicer.Tests;

public class ToolheadOrientationDragTest
{
    [Fact]
    public void The_X_ring_writes_panel_X()
    {
        var next = ToolheadOrientationDrag.Apply(a: 0f, b: 90f, c: 0f, axis: 0, deltaDeg: 25f);
        Assert.Equal(0f, next.A);
        Assert.Equal(90f, next.B);
        Assert.Equal(25f, next.C);
    }

    [Fact]
    public void The_Y_ring_writes_panel_Y()
    {
        var next = ToolheadOrientationDrag.Apply(a: 0f, b: 90f, c: 0f, axis: 1, deltaDeg: -15f);
        Assert.Equal(0f, next.A);
        Assert.Equal(75f, next.B);
        Assert.Equal(0f, next.C);
    }

    [Fact]
    public void The_Z_ring_writes_panel_Z()
    {
        var next = ToolheadOrientationDrag.Apply(a: 10f, b: 90f, c: 5f, axis: 2, deltaDeg: 20f);
        Assert.Equal(30f, next.A);
        Assert.Equal(90f, next.B);
        Assert.Equal(5f, next.C);
    }

    [Fact]
    public void A_drag_past_180_wraps_through_the_seam()
    {
        var next = ToolheadOrientationDrag.Apply(a: 0f, b: 90f, c: 170f, axis: 0, deltaDeg: 20f);
        Assert.Equal(-170f, next.C);

        var back = ToolheadOrientationDrag.Apply(a: -170f, b: 0f, c: 0f, axis: 2, deltaDeg: -20f);
        Assert.Equal(170f, back.A);
    }

    [Fact]
    public void The_ends_of_the_slider_stay_put()
    {
        Assert.Equal(180f, ToolheadOrientationDrag.Wrap(180f));
        Assert.Equal(-180f, ToolheadOrientationDrag.Wrap(-180f));
        Assert.Equal(180f, ToolheadOrientationDrag.Wrap(540f));
        Assert.Equal(-180f, ToolheadOrientationDrag.Wrap(-540f));
    }

    [Fact]
    public void An_unknown_axis_does_not_change_the_sliders()
    {
        var next = ToolheadOrientationDrag.Apply(a: 1f, b: 2f, c: 3f, axis: -1, deltaDeg: 40f);
        Assert.Equal((1f, 2f, 3f), next);
    }
}
