using MassiveSlicer.Viewport.Rendering;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public sealed class TcpAxisLabelLayoutTest
{
    [Fact]
    public void Build_names_tcp_and_flange_and_puts_xyz_on_tips()
    {
        var tcp = Matrix4.CreateTranslation(100f, 200f, 300f);
        var flange = Matrix4.CreateTranslation(0f, 0f, 0f);
        var tags = TcpAxisLabelLayout.Build(tcp, flange, sensor: null, tcpName: "T12 TCP");

        Assert.Contains(tags, t => t.IsTitle && t.Text == "T12 TCP");
        Assert.Contains(tags, t => t.IsTitle && t.Text == "FLANGE");
        Assert.Equal(2, tags.Count(t => t.Text == "x"));
        Assert.Equal(2, tags.Count(t => t.Text == "y"));
        Assert.Equal(2, tags.Count(t => t.Text == "z"));

        var tcpX = tags.Single(t => t.Text == "x" && t.World.X > 50f);
        Assert.InRange(tcpX.World.X, 100f + TcpAxisLabelLayout.AxisLengthMm, 100f + TcpAxisLabelLayout.AxisLengthMm + 40f);
        Assert.Equal(TcpAxisLabelLayout.ColorX, tcpX.ColorHex);
        Assert.Equal(TcpAxisLabelLayout.ColorY, tags.First(t => t.Text == "y").ColorHex);
        Assert.Equal(TcpAxisLabelLayout.ColorZ, tags.First(t => t.Text == "z").ColorHex);
    }

    [Fact]
    public void Build_empty_when_no_frames()
    {
        Assert.Empty(TcpAxisLabelLayout.Build(null, null, null, "T12"));
    }
}
