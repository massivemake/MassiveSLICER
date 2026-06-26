using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

public class ResumeRampPostProcessorTest
{
    [Fact]
    public void Apply_splits_extrusion_after_travel_into_ramp_steps()
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(1000, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(1000, 0, 10), new Vector3(2000, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(2000, 0, 10), new Vector3(3000, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });

        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var settings = new SliceSettings
        {
            BeadWidth       = 6f,
            LayerHeight     = 3f,
            PrintSpeedMps   = 0.1f,
            FlowRate        = 0.463f,
            ResumeRampEnabled         = true,
            ResumeRampStartSpeedMps   = 0.0005f,
            ResumeRampStartRpmPercent = 1f,
            ResumeRampDistanceMm      = 100f,
            ResumeRampSteps           = 10,
        };

        var result = ResumeRampPostProcessor.Apply(tp, settings);
        var moves  = result.Layers[0].Moves;

        Assert.Equal(13, moves.Count);
        Assert.Equal(MoveKind.Travel, moves[1].Kind);
        var ramp = moves.Skip(2).Take(10).ToList();
        Assert.All(ramp, m => Assert.True(m.IsResumeRamp));
        Assert.True(ramp[0].ResumeSpeedScale < ramp[^1].ResumeSpeedScale);
        Assert.True(ramp[0].ResumeRpmScale < ramp[^1].ResumeRpmScale);
        Assert.Equal(100f, ramp.Sum(m => Vector3.Distance(m.From, m.To)), 0.5f);
        Assert.Equal(MoveKind.Extrude, moves[12].Kind);
        Assert.False(moves[12].IsResumeRamp);
    }

    [Fact]
    public void Apply_disabled_leaves_toolpath_unchanged()
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(150, 0, 10), MoveKind.Extrude));

        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var result = ResumeRampPostProcessor.Apply(tp, new SliceSettings { ResumeRampEnabled = false });
        Assert.Equal(3, result.Layers[0].Moves.Count);
    }
}