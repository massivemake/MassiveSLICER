using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.Tests;

public sealed class JointGraphFrameTest
{
    [Fact]
    public void Zoom_keeps_the_center_anchor_under_the_cursor()
    {
        var next = JointGraphFrame.Home.Zoom(0.5, 0.5, 0.5);

        Assert.Equal(0.25, next.Left, 6);
        Assert.Equal(0.25, next.Bottom, 6);
        Assert.Equal(0.5, next.Width, 6);
        Assert.Equal(0.5, next.Height, 6);
        Assert.False(next.IsHome);
    }

    [Fact]
    public void Zoom_keeps_a_corner_anchor()
    {
        var next = JointGraphFrame.Home.Zoom(0, 0, 0.5);

        Assert.Equal(0, next.Left, 6);
        Assert.Equal(0, next.Bottom, 6);
        Assert.Equal(0.5, next.Width, 6);
        Assert.Equal(0.5, next.Height, 6);
    }

    [Fact]
    public void Zooming_back_out_lands_on_home()
    {
        var zoomed = JointGraphFrame.Home.Zoom(0.2, 0.8, 0.25);
        var home = zoomed.Zoom(0.2, 0.8, 8);

        Assert.True(home.IsHome);
        Assert.Equal(JointGraphFrame.Home, home);
    }

    [Fact]
    public void Pan_slides_the_window_and_stops_at_the_edge()
    {
        var zoomed = JointGraphFrame.Home.Zoom(0.5, 0.5, 0.5);
        var slid = zoomed.Pan(-0.1, 0.1);

        Assert.Equal(0.15, slid.Left, 6);
        Assert.Equal(0.35, slid.Bottom, 6);
        Assert.Equal(zoomed.Width, slid.Width, 6);
        Assert.Equal(zoomed.Height, slid.Height, 6);

        var pinned = zoomed.Pan(-5, 5);
        Assert.Equal(-zoomed.Width * JointGraphFrame.HorizontalSlack, pinned.Left, 6);
        Assert.Equal(0.5, pinned.Bottom, 6);
    }

    [Fact]
    public void Pan_can_slide_a_little_past_either_end()
    {
        var left = JointGraphFrame.Home.Pan(-0.02, 0);
        var right = JointGraphFrame.Home.Pan(0.02, 0);

        Assert.Equal(-0.02, left.Left, 6);
        Assert.Equal(0.02, right.Left, 6);
        Assert.Equal(1, left.Width, 6);
        Assert.False(left.IsHome);
        Assert.False(right.IsHome);

        var pinned = JointGraphFrame.Home.Pan(-1, 0);
        Assert.Equal(-JointGraphFrame.HorizontalSlack, pinned.Left, 6);
    }

    [Fact]
    public void Zoom_stops_at_one_column_and_five_percent_of_the_range()
    {
        var tight = JointGraphFrame.Home.Zoom(0.5, 0.5, 1e-9);

        Assert.Equal(JointGraphFrame.MinWidth, tight.Width, 9);
        Assert.Equal(JointGraphFrame.MinHeight, tight.Height, 9);
        Assert.True(tight.Left > 0);
        Assert.True(tight.Bottom > 0);
    }

    [Fact]
    public void Plot_and_data_round_trip_inside_the_window()
    {
        var frame = new JointGraphFrame(0.2, 0.4, 0.5, 0.25);

        Assert.Equal(0.45, frame.DataX(frame.PlotX(0.45)), 6);
        Assert.Equal(0.55, frame.DataY(frame.PlotY(0.55)), 6);
        Assert.Equal(0, frame.PlotX(0.2), 6);
        Assert.Equal(1, frame.PlotY(0.65), 6);
    }
}
