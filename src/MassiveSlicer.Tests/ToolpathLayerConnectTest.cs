using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public class ToolpathLayerConnectTest
{
    [Fact]
    public void Insert_far_xy_is_layer_change_travel()
    {
        var layer = new ToolpathLayer(1, 3.36f);
        layer.Moves.Add(new ToolpathMove(
            new Vector3(307.02f, -172.95f, 3.36f),
            new Vector3(310f, -172.95f, 3.36f),
            MoveKind.Extrude) { Normal = Vector3.UnitZ });

        ToolpathLayerConnect.Insert(layer, new Vector3(-276.65f, -327.46f, 0.22f), 8f);

        Assert.Equal(2, layer.Moves.Count);
        Assert.Equal(MoveKind.Travel, layer.Moves[0].Kind);
        Assert.True(layer.Moves[0].IsLayerChange);
        Assert.Equal(new Vector3(-276.65f, -327.46f, 0.22f), layer.Moves[0].From);
        Assert.Equal(new Vector3(307.02f, -172.95f, 3.36f), layer.Moves[0].To);
    }

    [Fact]
    public void Insert_near_xy_with_z_step_is_stitch()
    {
        var layer = new ToolpathLayer(1, 3f);
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 3), new Vector3(10, 0, 3), MoveKind.Extrude));
        ToolpathLayerConnect.Insert(layer, new Vector3(0.5f, 0, 0), 8f);
        Assert.Equal(MoveKind.Extrude, layer.Moves[0].Kind);
        Assert.True(layer.Moves[0].IsLayerStitch);
    }

    [Fact]
    public void Export_implicit_travel_when_next_layer_starts_far_away()
    {
        var tp = new Toolpath();
        var l0 = new ToolpathLayer(0, 0.22f) { PlaneNormal = Vector3.UnitZ };
        l0.Moves.Add(new ToolpathMove(new Vector3(-280, -327, 0.22f), new Vector3(-276.65f, -327.46f, 0.22f), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(l0);
        var l1 = new ToolpathLayer(1, 3.36f) { PlaneNormal = Vector3.UnitZ };
        l1.Moves.Add(new ToolpathMove(new Vector3(307.02f, -172.95f, 3.36f), new Vector3(310f, -172.95f, 3.36f), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(l1);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName            = "layer_hop",
            ExtrusionRpmPercent    = 24.94f,
            PrintSpeedMps          = 0.04f,
            TravelSpeedMps         = 0.6f,
            BeadWidthMm            = 8f,
            RobotModeEnabled       = true,
            TravelStartStopEnabled = true,
        });

        Assert.Contains(";layer change", krl);
        Assert.Contains(";travel start", krl);
        Assert.Contains("RPM = 0.00", krl);
        int lastLow = krl.LastIndexOf("X -276.65", StringComparison.Ordinal);
        int hop = krl.IndexOf(";layer change", StringComparison.Ordinal);
        int next = krl.IndexOf("X 307.02", StringComparison.Ordinal);
        Assert.True(lastLow >= 0 && hop > lastLow && next > hop);
        // Must not print the hop at print speed with RPM already on.
        var between = krl.Substring(lastLow, hop - lastLow);
        Assert.DoesNotContain("RPM = 24", between);
    }
}
