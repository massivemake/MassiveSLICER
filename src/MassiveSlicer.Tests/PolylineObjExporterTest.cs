using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class PolylineObjExporterTest
{
    [Fact]
    public void Chains_weld_connected_moves_and_split_on_a_gap()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f);
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 3), new Vector3(10, 0, 3), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(10, 0, 3), new Vector3(10, 20, 3), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 3), new Vector3(110, 0, 3), MoveKind.Travel));
        tp.Layers.Add(layer);

        var chains = PolylineObjExporter.Chains(tp, PolylineDrawFrame.Identity);

        Assert.Equal(2, chains.Count);
        Assert.Equal(3, chains[0].Count);
        Assert.Equal(new Vector3(10, 20, 3), chains[0][2]);
        Assert.Equal(2, chains[1].Count);
        Assert.Equal(new Vector3(100, 0, 3), chains[1][0]);
    }

    [Fact]
    public void DrawPoint_matches_viewport_when_local_is_the_centroid()
    {
        var origin = new Vector3(2000f, 10f, 900f);
        var frame = new PolylineDrawFrame(
            origin,
            1, 0, 0, origin.X,
            0, 1, 0, origin.Y,
            0, 0, 1, origin.Z);

        var drawn = PolylineObjExporter.DrawPoint(new Vector3(2010f, 10f, 903f), frame);

        Assert.Equal(2010f, drawn.X, 3);
        Assert.Equal(10f, drawn.Y, 3);
        Assert.Equal(903f, drawn.Z, 3);
    }

    [Fact]
    public void Write_emits_obj_lines_in_millimeters()
    {
        var chains = new List<List<Vector3>>
        {
            new() { new Vector3(0, 0, 3), new Vector3(10, 0, 3), new Vector3(10, 20, 3) },
        };

        var text = PolylineObjExporter.Write("curtain src", chains);

        Assert.Contains("# units: millimeters", text, StringComparison.Ordinal);
        Assert.Contains("o curtain_src", text, StringComparison.Ordinal);
        Assert.Contains("v 0.000 0.000 3.000", text, StringComparison.Ordinal);
        Assert.Contains("v 10.000 0.000 3.000", text, StringComparison.Ordinal);
        Assert.Contains("l 1 2", text, StringComparison.Ordinal);
        Assert.Contains("l 2 3", text, StringComparison.Ordinal);
        Assert.DoesNotContain(text.Split('\n'), line => line.StartsWith("f ", StringComparison.Ordinal));
    }
}
