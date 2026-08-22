using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class CodeEditorSrcInjectorTest
{
    private static Toolpath BeadThenTravel(bool wipe)
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        if (wipe)
        {
            layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(108, 0, 10), MoveKind.Extrude)
            {
                Normal = Vector3.UnitZ,
                IsWipe = true,
            });
        }
        layer.Moves.Add(new ToolpathMove(new Vector3(wipe ? 108 : 100, 0, 10), new Vector3(200, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(200, 0, 10), new Vector3(250, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);
        return tp;
    }

    [Fact]
    public void Inject_after_travel_end_and_before_travel_start()
    {
        var krl = KrlExporter.Export(BeadThenTravel(wipe: false), new KrlExportSettings
        {
            ProgramName = "ce_inject",
            PrintSpeedMps = 0.020f,
            ExtrusionRpmPercent = 50f,
            TravelStartStopEnabled = true,
            CodeEditorInject = new CodeEditorInjectSettings { PointLoaderSafeIo = true, SpeedMmS = 20 },
        });

        Assert.Contains(";travel start", krl);
        Assert.Contains(";travel end", krl);
        Assert.Contains("$OUT[7] = TRUE; !Modified by RCE!", krl);
        Assert.Contains("$OUT[7] = FALSE; !Modified by RCE!", krl);
        Assert.DoesNotContain("TRIGGER WHEN DISTANCE", krl);

        int firstEnd = krl.IndexOf(";travel end", StringComparison.Ordinal);
        int startIo  = krl.IndexOf("$OUT[7] = TRUE; !Modified by RCE!", firstEnd, StringComparison.Ordinal);
        int travel   = krl.IndexOf(";travel start", StringComparison.Ordinal);
        int stopIo   = krl.IndexOf("$OUT[7] = FALSE; !Modified by RCE!", StringComparison.Ordinal);
        Assert.True(startIo > firstEnd && startIo < travel);
        Assert.True(stopIo > startIo && stopIo < travel);
    }

    [Fact]
    public void Wipe_opens_travel_start()
    {
        var krl = KrlExporter.Export(BeadThenTravel(wipe: true), new KrlExportSettings
        {
            ProgramName = "ce_wipe",
            PrintSpeedMps = 0.020f,
            ExtrusionRpmPercent = 50f,
            TravelStartStopEnabled = true,
        });

        int travel = krl.IndexOf(";travel start", StringComparison.Ordinal);
        int wipe   = krl.IndexOf(";wipe", StringComparison.Ordinal);
        Assert.True(travel >= 0 && wipe > travel, "wipe must sit inside an open travel");
    }

    [Fact]
    public void PointLoaderSafe_off_keeps_trigger()
    {
        var krl = KrlExporter.Export(BeadThenTravel(wipe: false), new KrlExportSettings
        {
            ProgramName = "ce_trig",
            PrintSpeedMps = 0.020f,
            ExtrusionRpmPercent = 50f,
            TravelStartStopEnabled = true,
            CodeEditorInject = new CodeEditorInjectSettings { PointLoaderSafeIo = false, SpeedMmS = 20 },
        });

        Assert.Contains("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $OUT[7] = FALSE", krl);
    }

    [Fact]
    public void Stop_vel_cp_is_half_of_print_speed()
    {
        var krl = KrlExporter.Export(BeadThenTravel(wipe: false), new KrlExportSettings
        {
            ProgramName = "ce_half",
            PrintSpeedMps = 0.060f,
            ExtrusionRpmPercent = 50f,
            TravelStartStopEnabled = true,
            CodeEditorInject = new CodeEditorInjectSettings { PointLoaderSafeIo = true },
        });

        Assert.Contains("$VEL.CP = 0.030000; !Modified by RCE!", krl);
        Assert.DoesNotContain("$VEL.CP = 0.050000; !Modified by RCE!", krl);
    }

    [Fact]
    public void Stop_after_inserts_past_travel_start()
    {
        var krl = KrlExporter.Export(BeadThenTravel(wipe: false), new KrlExportSettings
        {
            ProgramName = "ce_after",
            PrintSpeedMps = 0.020f,
            ExtrusionRpmPercent = 50f,
            TravelStartStopEnabled = true,
            CodeEditorInject = new CodeEditorInjectSettings
            {
                PointLoaderSafeIo = true,
                StopDirection = "After",
                StopDistance = 0,
                StopUnits = "Millimeters",
            },
        });

        int travel = krl.IndexOf(";travel start", StringComparison.Ordinal);
        int stopIo = krl.IndexOf("$OUT[7] = FALSE; !Modified by RCE!", StringComparison.Ordinal);
        Assert.True(travel >= 0 && stopIo > travel, "After + 0 mm = immediately after ;travel start");
    }

    [Fact]
    public void WithHalfPrintVel_rewrites_existing_line()
    {
        Assert.Equal(
            "TRIGGER WHEN DISTANCE=0 DELAY=0 DO $OUT[7] = FALSE\n$VEL.CP = 0.038000",
            CodeEditorInjectSettings.WithHalfPrintVel(
                "TRIGGER WHEN DISTANCE=0 DELAY=0 DO $OUT[7] = FALSE\n$VEL.CP = 0.030000",
                0.076));
    }

    [Fact]
    public void DistanceMm_converts_time_with_speed()
    {
        Assert.Equal(7.0, CodeEditorInjectSettings.DistanceMm("Milliseconds", 350, 20), 6);
        Assert.Equal(26.6, CodeEditorInjectSettings.DistanceMm("Milliseconds", 350, 76), 6);
    }
}
