using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class ToolpathSerializerTest
{
    [Fact]
    public void RoundTrip_preserves_layers_and_moves()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f)
        {
            Height      = 3f,
            PlaneNormal = new Vector3(0, 0, 1),
        };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            Normal        = new Vector3(0, 0, 1),
            IsLayerChange = false,
            IsLayerStitch = true,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(100, 100, 10), MoveKind.Travel)
        {
            IsLayerChange = true,
        });
        tp.Layers.Add(layer);

        var restored = ToolpathSerializer.FromData(ToolpathSerializer.ToData(tp));

        Assert.Single(restored.Layers);
        var rl = restored.Layers[0];
        Assert.Equal(0, rl.Index);
        Assert.Equal(10f, rl.Z, precision: 3);
        Assert.Equal(3f, rl.Height, precision: 3);
        Assert.Equal(Vector3.UnitZ, rl.PlaneNormal);
        Assert.Equal(2, rl.Moves.Count);
        Assert.Equal(MoveKind.Extrude, rl.Moves[0].Kind);
        Assert.True(rl.Moves[0].IsLayerStitch);
        Assert.Equal(MoveKind.Travel, rl.Moves[1].Kind);
        Assert.True(rl.Moves[1].IsLayerChange);
    }
}