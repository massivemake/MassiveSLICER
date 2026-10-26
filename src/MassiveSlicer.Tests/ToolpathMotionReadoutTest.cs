using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class ToolpathMotionReadoutTest
{
    static KrlExportSettings Settings(float printMps, float rpm, float firstSpeed = 0, float firstRpm = 0) => new()
    {
        ProgramName = "t",
        PrintSpeedMps = printMps,
        ExtrusionRpmPercent = rpm,
        BeadWidthMm = 6,
        LayerHeightMm = 3,
        FlowRate = 0.463f,
        FirstLayerSpeedMps = firstSpeed,
        FirstLayerRpmPercent = firstRpm,
    };

    static ToolpathMove Extrude() => new(Vector3.Zero, Vector3.UnitX, MoveKind.Extrude);

    [Fact]
    public void Extrude_uses_panel_speed_and_rpm()
    {
        var demand = ToolpathMotionReadout.ForExtrude(Extrude(), Settings(0.08f, 42f));
        Assert.Equal(80, demand.SpeedMmS, 3);
        Assert.Equal(42f, demand.RpmPercent);
    }

    [Fact]
    public void Scale_multiplies_speed_and_rpm()
    {
        var move = Extrude() with { PrintSpeedScale = 0.5f };
        var demand = ToolpathMotionReadout.ForExtrude(move, Settings(0.08f, 40f));
        Assert.Equal(40, demand.SpeedMmS, 3);
        Assert.Equal(20f, demand.RpmPercent);
    }

    [Fact]
    public void Height_scale_changes_rpm_and_leaves_robot_speed()
    {
        var move = Extrude() with { HeightScale = 0.5f };
        var demand = ToolpathMotionReadout.ForExtrude(move, Settings(0.1f, 50f));
        Assert.Equal(100, demand.SpeedMmS, 3);
        Assert.Equal(25f, demand.RpmPercent);
    }

    [Fact]
    public void First_layer_settings_replace_the_nominal_only_on_layer_zero()
    {
        var move = Extrude();
        var settings = Settings(0.08f, 40f, firstSpeed: 0.05f, firstRpm: 25f);
        var first = ToolpathMotionReadout.ForExtrude(move, ToolpathRpm.ForLayer(settings, 0));
        var later = ToolpathMotionReadout.ForExtrude(move, ToolpathRpm.ForLayer(settings, 3));
        Assert.Equal(50, first.SpeedMmS, 3);
        Assert.Equal(25f, first.RpmPercent);
        Assert.Equal(80, later.SpeedMmS, 3);
        Assert.Equal(40f, later.RpmPercent);
    }

    [Fact]
    public void Absolute_rpm_override_ignores_the_panel_and_the_scale()
    {
        var move = Extrude() with { PrintSpeedScale = 0.5f, RpmPercentOverride = 80f };
        var demand = ToolpathMotionReadout.ForExtrude(move, Settings(0.08f, 40f));
        Assert.Equal(40, demand.SpeedMmS, 3);
        Assert.Equal(80f, demand.RpmPercent);
    }
}
