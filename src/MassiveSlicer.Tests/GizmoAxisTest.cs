using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public class GizmoAxisTest
{
    [Theory]
    [InlineData(GizmoAxis.XY, 0, 1, 2)]
    [InlineData(GizmoAxis.YZ, 1, 2, 0)]
    [InlineData(GizmoAxis.XZ, 0, 2, 1)]
    public void A_band_spans_two_axes_and_leaves_the_third_alone(GizmoAxis band, int a, int b, int normal)
    {
        // Getting the normal wrong would let a band quietly drag along the axis it is supposed to
        // hold fixed — the exact thing a two-axis handle exists to prevent.
        Assert.True(band.IsPlane());
        Assert.Equal((a, b, normal), band.PlaneAxes());
        Assert.NotEqual(normal, a);
        Assert.NotEqual(normal, b);
    }

    [Theory]
    [InlineData(GizmoAxis.None)]
    [InlineData(GizmoAxis.X)]
    [InlineData(GizmoAxis.Y)]
    [InlineData(GizmoAxis.Z)]
    [InlineData(GizmoAxis.All)]
    public void Everything_else_is_not_a_plane(GizmoAxis axis)
        => Assert.False(axis.IsPlane());
}
