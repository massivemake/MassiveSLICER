using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

public class MovementPostProcessorTest
{
    [Fact]
    public void Apply_inserts_wipe_and_z_hop_before_travel()
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));

        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var settings = new SliceSettings
        {
            ZHopMm       = 5f,
            WipeMode     = WipeMode.Retrace,
            WipeLengthMm = 8f,
            WipeRampMm   = 4f,
        };

        var result = MovementPostProcessor.Apply(tp, settings);
        var moves  = result.Layers[0].Moves;

        Assert.True(moves.Exists(m => m.IsWipe));
        Assert.Equal(3, moves.Count(m => m.IsZHop));
        Assert.True(moves.FindIndex(m => m.IsWipe) < moves.FindIndex(m => m.IsZHop));
    }
}