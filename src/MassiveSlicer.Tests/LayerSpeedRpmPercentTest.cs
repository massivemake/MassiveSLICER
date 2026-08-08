using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

/// <summary>
/// Adaptive speed stated as extruder RPM percent instead of robot mm/s.
///
/// Numbers here come from a real job: Dragon column bottom, 8 mm bead, 3 mm nominal layer,
/// PPGF on the HF head (flow 0.5863 rev/cm³), 60 mm/s print speed. That combination commands
/// 50.66 % RPM at full thickness, which is why 99 mm/s reached only 83.6 % and the 99 % export
/// gate sits at 117 mm/s.
/// </summary>
public class LayerSpeedRpmPercentTest
{
    private const float Bead = 8f, Nominal = 3f, Flow = 0.5863f, BaseSpeedMmS = 60f;

    private static SliceSettings Settings(
        bool useRpm, float minPct = 85f, float maxPct = 85f, float robotMax = 0f,
        float flow = Flow) => new()
    {
        BeadWidth               = Bead,
        LayerHeight             = Nominal,
        PrintSpeedMps           = BaseSpeedMmS / 1000f,
        FlowRate                = flow,
        TravelSpeed             = 0.12f,
        WipeSpeed               = 0.12f,
        LayerSpeedAdaptEnabled  = true,
        LayerSpeedMinMmS        = BaseSpeedMmS,
        LayerSpeedMaxMmS        = 99f,
        LayerSpeedUseRpmPercent = useRpm,
        LayerSpeedMinRpmPercent = minPct,
        LayerSpeedMaxRpmPercent = maxPct,
        LayerSpeedRobotMaxMmS   = robotMax,
    };

    /// <summary>Two layers of different thickness, each a single 100 mm bead.</summary>
    private static Toolpath TwoLayers(float thinHeight, float thickHeight)
    {
        var tp = new Toolpath();
        foreach (var (i, h) in new[] { (0, thickHeight), (1, thinHeight) })
        {
            var layer = new ToolpathLayer(i, 10f + i) { Height = h };
            layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(100, 0, 0), MoveKind.Extrude)
            {
                HeightScale = h / Nominal,
            });
            tp.Layers.Add(layer);
        }
        return tp;
    }

    [Fact]
    public void The_inverse_agrees_with_the_rpm_formula()
    {
        // Guards the pair against drifting: round-trip a speed through both directions.
        float rpm   = KrlAnout.ComputeRpmPercent(Bead, Nominal, 0.117f, Flow);
        float speed = KrlAnout.SpeedMmSForRpmPercent(rpm, Bead, Nominal, Flow);
        Assert.Equal(117f, speed, 2);
        // And the documented anchors for this material.
        Assert.Equal(50.66f, KrlAnout.ComputeRpmPercent(Bead, Nominal, 0.060f, Flow), 2);
        Assert.Equal(117f, KrlAnout.SpeedMmSForRpmPercent(99f, Bead, Nominal, Flow), 0);
    }

    [Fact]
    public void A_thin_layer_gets_more_speed_to_reach_the_same_flow()
    {
        // Robot ceiling lifted well clear so the RPM target is what binds.
        var tp = LayerSpeedPostProcessor.Apply(TwoLayers(1f, 3f), Settings(true, 85f, 85f, robotMax: 1000f));

        float thickScale = tp.Layers[0].Moves[0].PrintSpeedScale;
        float thinScale  = tp.Layers[1].Moves[0].PrintSpeedScale;

        // A third the thickness needs three times the speed for the same flow.
        Assert.Equal(3f, thinScale / thickScale, 2);
        // And the absolute speed is whatever 85 % works out to for a full-thickness layer
        // (~100.7 mm/s here) — taken from the formula rather than a rounded-off constant.
        float expected = KrlAnout.SpeedMmSForRpmPercent(85f, Bead, Nominal, Flow) / BaseSpeedMmS;
        Assert.Equal(expected, thickScale, 3);
        Assert.InRange(thickScale * BaseSpeedMmS, 100f, 101f);
    }

    [Fact]
    public void Commanded_rpm_lands_on_the_target_on_every_layer()
    {
        // The point of the mode: the number the operator typed is the number the export writes.
        var tp = LayerSpeedPostProcessor.Apply(TwoLayers(1f, 3f), Settings(true, 85f, 85f, robotMax: 1000f));
        var export = new KrlExportSettings
        {
            ProgramName   = "T",
            BeadWidthMm   = Bead,
            LayerHeightMm = Nominal,
            PrintSpeedMps = BaseSpeedMmS / 1000f,
            FlowRate      = Flow,
        };

        foreach (var layer in tp.Layers)
            Assert.Equal(85f, ToolpathRpm.MovePercent(layer.Moves[0], export), 1);
    }

    [Fact]
    public void The_robot_ceiling_wins_when_the_extruder_still_has_room()
    {
        // A 1 mm layer would want ~301 mm/s at 85 %. The arm caps it, and flow falls short —
        // correctly, because the alternative is commanding a speed the machine cannot hold.
        var tp = LayerSpeedPostProcessor.Apply(TwoLayers(1f, 3f), Settings(true, 85f, 85f, robotMax: 120f));

        float thinSpeed = tp.Layers[1].Moves[0].PrintSpeedScale * BaseSpeedMmS;
        Assert.Equal(120f, thinSpeed, 1);
    }

    [Fact]
    public void Falls_back_to_the_mm_s_ceiling_when_no_robot_maximum_is_set()
    {
        var tp = LayerSpeedPostProcessor.Apply(TwoLayers(1f, 3f), Settings(true, 85f, 85f, robotMax: 0f));
        // LayerSpeedMaxMmS is 99 in these settings, so that becomes the cap.
        Assert.Equal(99f, tp.Layers[1].Moves[0].PrintSpeedScale * BaseSpeedMmS, 1);
    }

    [Fact]
    public void Mm_per_second_mode_is_untouched()
    {
        // Control: the existing behaviour must be bit-identical when the new mode is off.
        var before = LayerSpeedPostProcessor.Apply(TwoLayers(1f, 3f), Settings(false));
        Assert.Equal(99f / BaseSpeedMmS, before.Layers[0].Moves[0].PrintSpeedScale, 3);
        // Both layers carry one equal-length bead, so cut length ties and both take the high end.
        Assert.Equal(before.Layers[0].Moves[0].PrintSpeedScale,
                     before.Layers[1].Moves[0].PrintSpeedScale, 3);
    }

    [Fact]
    public void A_zero_flow_rate_cannot_stall_the_machine()
    {
        var s = Settings(true, 85f, 85f, robotMax: 1000f, flow: 0f);
        var tp = LayerSpeedPostProcessor.Apply(TwoLayers(1f, 3f), s);
        foreach (var layer in tp.Layers)
            Assert.Equal(1f, layer.Moves[0].PrintSpeedScale, 3);
    }
}
