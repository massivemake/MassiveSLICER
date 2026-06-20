using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class KrlExporterTest
{
    [Fact]
    public void Export_header_and_extrude_emit_correct_anout_literals()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f)
        {
            Height      = 3f,
            PlaneNormal = Vector3.UnitZ,
        };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var settings = new KrlExportSettings
        {
            ProgramName    = "test_print",
            Temperature1   = 220f,
            Temperature2   = 220f,
            Temperature3   = 220f,
            BeadWidthMm    = 6f,
            LayerHeightMm  = 3f,
            FlowRate       = 0.463f,
            PrintSpeedMps  = 0.1f,
            TravelSetAnout4Zero = false,
        };

        var krl = KrlExporter.Export(tp, settings);

        Assert.Contains("$ANOUT[1] = 0.2272 ; T1 = 220C", krl);
        Assert.Contains("$ANOUT[4] = 0.001 ; RPM idle", krl);
        Assert.Contains("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]=0.5 ; RPM on", krl);
    }

    [Fact]
    public void Export_first_extrusion_emits_start_wait_after_rpm_on()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f)
        {
            Height      = 3f,
            PlaneNormal = Vector3.UnitZ,
        };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var settings = new KrlExportSettings
        {
            ProgramName           = "test_wait",
            ExtrusionRpmPercent   = 50f,
            ExtrusionStartWaitSec = 1f,
            TravelSetAnout4Zero   = false,
        };

        var krl = KrlExporter.Export(tp, settings);

        Assert.Contains("$ANOUT[4] = 0.5 ; RPM on", krl);
        Assert.Contains("WAIT SEC 1", krl);
        Assert.DoesNotContain("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]=0.5 ; RPM on", krl);

        int rpmIdx  = krl.IndexOf("$ANOUT[4] = 0.5 ; RPM on", StringComparison.Ordinal);
        int waitIdx = krl.IndexOf("WAIT SEC 1", StringComparison.Ordinal);
        int velIdx  = krl.IndexOf("$VEL.CP", waitIdx, StringComparison.Ordinal);
        Assert.True(rpmIdx >= 0 && waitIdx > rpmIdx && velIdx > waitIdx);
    }

    [Fact]
    public void Export_resume_after_travel_emits_wait_before_extrude()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(150, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var settings = new KrlExportSettings
        {
            ProgramName            = "test_resume_wait",
            ExtrusionRpmPercent    = 50f,
            ExtrusionStartWaitSec  = 0f,
            ExtrusionResumeWaitSec = 0.5f,
            TravelSetAnout4Zero    = true,
        };

        var krl = KrlExporter.Export(tp, settings);
        Assert.Contains("WAIT SEC 0.5", krl);
        int travelIdx = krl.IndexOf(";travel", StringComparison.Ordinal);
        int waitIdx   = krl.IndexOf("WAIT SEC 0.5", StringComparison.Ordinal);
        int rpmIdx    = krl.LastIndexOf("$ANOUT[4] = 0.5 ; RPM on", StringComparison.Ordinal);
        Assert.True(travelIdx < waitIdx);
        Assert.True(rpmIdx < waitIdx);
    }

    [Fact]
    public void Export_later_extrusion_resume_does_not_emit_start_wait()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f)
        {
            Height      = 3f,
            PlaneNormal = Vector3.UnitZ,
        };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(150, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var settings = new KrlExportSettings
        {
            ProgramName           = "test_wait_once",
            ExtrusionRpmPercent   = 50f,
            ExtrusionStartWaitSec = 1f,
            TravelSetAnout4Zero   = true,
        };

        var krl = KrlExporter.Export(tp, settings);
        Assert.Equal(1, krl.Split("WAIT SEC 1").Length - 1);
    }

    [Fact]
    public void Export_extrusion_rpm_percent_override_applies_offset()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f)
        {
            Height      = 3f,
            PlaneNormal = Vector3.UnitZ,
        };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var settings = new KrlExportSettings
        {
            ProgramName         = "test_offset",
            ExtrusionRpmPercent = 60f,
            TravelSetAnout4Zero = false,
        };

        var krl = KrlExporter.Export(tp, settings);

        Assert.Contains("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]=0.6 ; RPM on", krl);
    }
}