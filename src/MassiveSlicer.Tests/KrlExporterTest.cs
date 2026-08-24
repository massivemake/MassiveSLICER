using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
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
        };

        var krl = KrlExporter.Export(tp, settings);

        Assert.Contains("$ANOUT[1] = 0.2272 ; T1 = 220C", krl);
        Assert.Contains("$ANOUT[4] = 0.001 ; RPM idle", krl);
        // Geometry RPM ~50.004% → ceiling to whole percent → 0.51
        Assert.Contains("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]=0.51 ; RPM on", krl);
    }

    [Fact]
    public void Export_holds_rail_E1_through_every_lin_move()
    {
        // Regression: LIN moves hard-coded E1 0.000, so the rail snapped from the
        // home PTP position back to zero on the first motion of the print.
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(100, 50, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 50, 10), new Vector3(0, 50, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName = "rail_hold",
            HomeE1Mm    = -439.08f,
        });

        Assert.Contains("E1 -439.080", krl); // home PTP
        foreach (var line in krl.Split('\n'))
            if (line.TrimStart().StartsWith("LIN "))
                Assert.Contains("E1 -439.080", line);

        // Without a rail, LIN keeps the legacy E1 0.000.
        var noRail = KrlExporter.Export(tp, new KrlExportSettings { ProgramName = "no_rail" });
        foreach (var line in noRail.Split('\n'))
            if (line.TrimStart().StartsWith("LIN "))
                Assert.Contains("E1 0.000", line);
    }

    [Fact]
    public void Export_applies_toolhead_orientation_offset_on_flat_toolpath()
    {
        // Regression: a deliberate global toolhead spin must survive the B≈90° gimbal-lock
        // branch on a flat top-down toolpath. Previously A and C were zeroed there, silently
        // dropping the toolhead rotation the viewport preview shows.
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        static float FirstA(string krl)
        {
            var m = Regex.Match(krl, @"LIN \{X [^}]*?A (-?\d+\.\d+)");
            Assert.True(m.Success, "expected a LIN line with an A angle");
            return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        // No toolhead offset → A stays 0 (noise-suppressing gimbal zero preserved).
        var noOffset = KrlExporter.Export(tp, new KrlExportSettings { ProgramName = "t0" });
        Assert.Equal(0f, FirstA(noOffset), 1);

        // 43° toolhead offset (X slider → ToolheadOffsetC) → A reflects the spin, not zeroed.
        var withOffset = KrlExporter.Export(tp, new KrlExportSettings { ProgramName = "t1", ToolheadOffsetC = 43f });
        Assert.True(Math.Abs(FirstA(withOffset)) > 40f,
            $"toolhead offset should propagate to exported A, got {FirstA(withOffset)}");
    }

    [Fact]
    public void Export_uses_crlf_line_endings_on_every_platform()
    {
        // KUKA KRC / PointLoader are Windows tools and mis-parse LF-only .src files.
        // StringBuilder.AppendLine emits LF on macOS/Linux, so Export must normalize to CRLF.
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings { ProgramName = "crlf_test" });

        Assert.Contains("\r\n", krl);
        Assert.DoesNotContain("\n", krl.Replace("\r\n", "")); // no bare LF remains after stripping CRLF
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
        };

        var krl = KrlExporter.Export(tp, settings);

        Assert.Contains("$ANOUT[4] = 0.50 ; RPM on", krl);
        Assert.Contains("WAIT SEC 1", krl);
        Assert.DoesNotContain("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]=0.50 ; RPM on", krl);

        int rpmIdx  = krl.IndexOf("$ANOUT[4] = 0.50 ; RPM on", StringComparison.Ordinal);
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
        };

        var krl = KrlExporter.Export(tp, settings);
        Assert.Contains("WAIT SEC 0.5", krl);
        int travelIdx = krl.IndexOf(";travel", StringComparison.Ordinal);
        int waitIdx   = krl.IndexOf("WAIT SEC 0.5", StringComparison.Ordinal);
        int rpmIdx    = krl.LastIndexOf("$ANOUT[4] = 0.50 ; RPM on", StringComparison.Ordinal);
        Assert.True(travelIdx < waitIdx);
        Assert.True(rpmIdx < waitIdx);
    }

    [Fact]
    public void Export_per_move_ResumeWaitSec_overrides_global()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel)
            { ResumeWaitSec = 0.25f });
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(150, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName            = "test_per_move_wait",
            ExtrusionRpmPercent    = 50f,
            ExtrusionStartWaitSec  = 0f,
            ExtrusionResumeWaitSec = 0.5f,
        });
        Assert.Contains("WAIT SEC 0.25", krl);
        Assert.DoesNotContain("WAIT SEC 0.5", krl);
    }

    [Fact]
    public void Export_DigitalStartStop_uses_Caracol_injector_OUT7_OUT9_around_travel()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(150, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        // Post-process LFAM header must not win; Caracol S&S injector pattern must.
        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName             = "test_dss",
            ExtrusionRpmPercent     = 50f,
            Temperature1            = 250f,
            Temperature2            = 250f,
            Temperature3            = 250f,
            ExtrusionStartWaitSec   = 0f,
            ExtrusionResumeWaitSec  = 0.5f,
            SsPreTravelWaitSec      = 0.5f,
            SsApproachSpeedScale    = 0.5f,
            DigitalStartStopEnabled = true,
            HeaderTemplate          = KrlExporter.DefaultHeaderTemplate,
            FooterTemplate          = KrlExporter.DefaultFooterTemplate,
        });

        Assert.Contains(";FOLD CaracolSafety", krl);
        Assert.Contains(";FOLD MAT out of INI", krl);
        Assert.Contains("T1 = 250", krl);
        // Re-latch guard: nudge (target-5) before the target so ANALOGHANDLER always re-writes.
        Assert.Contains("T1 = 245", krl);
        Assert.True(krl.IndexOf("T1 = 245", System.StringComparison.Ordinal)
                    < krl.IndexOf("T1 = 250", System.StringComparison.Ordinal),
                    "nudge must precede the target temperature");
        Assert.Contains("MassiveSLICER", krl);
        Assert.Contains(";ULTRARESPONSIVE MODE", krl); // footer
        Assert.DoesNotContain("$ANOUT[1]", krl);
        Assert.DoesNotContain("$ANOUT[4]", krl);

        // Header: URM (OUT[8]) init FALSE; robot-mode gate (OUT[9]) latched TRUE in MAT.
        Assert.Contains("$OUT[8] = FALSE", krl);
        Assert.Contains("$OUT[9] = TRUE", krl);
        // Travel Moves: RPM off at travel, RPM on just before ;travel end. No RCE inject.
        Assert.Contains(";travel start", krl);
        Assert.Contains(";travel end", krl);
        Assert.DoesNotContain("; !Modified by RCE!", krl);
        Assert.DoesNotContain("TRIGGER WHEN DISTANCE", krl);
        Assert.DoesNotContain("$OUT[7] = TRUE; !Modified by RCE!", krl);

        int travel = krl.IndexOf(";travel start", StringComparison.Ordinal);
        int rpmOff = krl.IndexOf("RPM = 0.00", travel, StringComparison.Ordinal);
        int rpmOn  = krl.IndexOf("RPM = 50", travel, StringComparison.Ordinal);
        int travelEnd = krl.IndexOf(";travel end", travel, StringComparison.Ordinal);
        Assert.True(travel >= 0 && rpmOff > travel && rpmOn > rpmOff && travelEnd > rpmOn);
    }

    [Fact]
    public void Export_RobotMode_only_sets_temps_and_rpm_without_travel_start_stop()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(150, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName            = "test_robot_only",
            ExtrusionRpmPercent    = 50f,
            Temperature1           = 250f,
            RobotModeEnabled       = true,
            TravelStartStopEnabled = false,
            HeaderTemplate         = KrlExporter.DefaultHeaderTemplate,
        });

        Assert.Contains(";FOLD CaracolSafety", krl);
        Assert.Contains("T1 = 250", krl);
        Assert.DoesNotContain("$ANOUT[1]", krl);
        Assert.Contains("RPM =", krl);
        Assert.DoesNotContain(";travel start", krl);
        Assert.DoesNotContain(";digital start/stop - stop (Caracol URM)", krl);
        Assert.Contains(";travel", krl);
    }

    [Fact]
    public void Export_extruder_air_writes_out5_on_in_header_and_off_in_footer()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var on = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName        = "air_on",
            ExtrusionRpmPercent = 50f,
            ExtruderAirEnabled = true,
            HeaderTemplate     = KrlExporter.DefaultHeaderTemplate,
            FooterTemplate     = KrlExporter.DefaultFooterTemplate,
        });
        int onIdx   = on.IndexOf("$OUT[5] = TRUE", StringComparison.Ordinal);
        int firstLin = on.IndexOf("LIN ", StringComparison.Ordinal);
        int retreat = on.IndexOf(";retreat", StringComparison.Ordinal);
        int offIdx  = on.LastIndexOf("$OUT[5] = FALSE", StringComparison.Ordinal);
        Assert.True(onIdx >= 0 && onIdx < firstLin, "air on must be in the header");
        Assert.True(offIdx > retreat, "air off must be in the footer");
        Assert.Contains(";extruder air on", on);
        Assert.Contains(";extruder air off", on);

        var off = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName        = "air_off",
            ExtrusionRpmPercent = 50f,
            ExtruderAirEnabled = false,
            HeaderTemplate     = KrlExporter.DefaultHeaderTemplate,
            FooterTemplate     = KrlExporter.DefaultFooterTemplate,
        });
        Assert.DoesNotContain("$OUT[5]", off);
    }

    [Fact]
    public void Export_TravelMoves_only_emits_start_stop_without_forcing_robot_mat()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(150, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName            = "test_travel_only",
            ExtrusionRpmPercent    = 50f,
            RobotModeEnabled       = false,
            TravelStartStopEnabled = true,
        });

        Assert.Contains(";travel start", krl);
        Assert.Contains(";travel end", krl);
        Assert.Contains("RPM = 0.00", krl);
        Assert.Contains("RPM = 50", krl);
        Assert.DoesNotContain("; !Modified by RCE!", krl);
        Assert.DoesNotContain(";FOLD CaracolSafety", krl);
        Assert.Contains("$ANOUT[1]", krl);
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
        };

        var krl = KrlExporter.Export(tp, settings);

        Assert.Contains("TRIGGER WHEN DISTANCE=0 DELAY=0 DO $ANOUT[4]=0.60 ; RPM on", krl);
    }

    [Fact]
    public void Export_resume_ramp_emits_stepped_speed_and_rpm()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(60, 0, 10), MoveKind.Extrude)
        {
            IsResumeRamp     = true,
            ResumeSpeedScale = 0.005f,
            ResumeRpmScale   = 0.02f,
            Normal           = Vector3.UnitZ,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(60, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            IsResumeRamp     = true,
            ResumeSpeedScale = 1f,
            ResumeRpmScale   = 1f,
            Normal           = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var settings = new KrlExportSettings
        {
            ProgramName         = "test_ramp",
            ExtrusionRpmPercent = 50f,
            PrintSpeedMps       = 0.1f,
        };

        var krl = KrlExporter.Export(tp, settings);

        Assert.Contains("$ANOUT[4] = 0.01 ; RPM ramp", krl);
        Assert.Contains("$VEL.CP = 0.000500", krl);
        Assert.Contains("$ANOUT[4] = 0.50 ; RPM ramp", krl);
        Assert.Contains("$VEL.CP = 0.100000", krl);
    }

    [Fact]
    public void Export_lfam1_slice_bed_lift_maps_visual_bed_to_kuka_base_z()
    {
        // LFAM 1: visual slice plane at origin.z=272.93, KUKA BASE Z=0 at robroot+baseData=778.
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f)
        {
            Height      = 3f,
            PlaneNormal = Vector3.UnitZ,
        };
        layer.Moves.Add(new ToolpathMove(
            new Vector3(1500, 900, 275.93f),
            new Vector3(1600, 900, 275.93f),
            MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var settings = new KrlExportSettings
        {
            ProgramName        = "lfam1_z",
            RobrootWorldPos    = new Vector3(0, 0, 500),
            BaseDataOffset     = new Vector3(1496.36f, -577.89f, 278f),
            SliceBedWorldZ     = 272.93f,
            ApproachZMm        = 50f,
        };

        var krl = KrlExporter.Export(tp, settings);

        var zValues = Regex.Matches(krl, @"Z (-?\d+\.\d+)")
            .Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Take(3)
            .ToArray();

        Assert.Equal(3, zValues.Length);
        Assert.InRange(zValues[0], 52f, 54f);   // approach: layer Z 3 + 50 mm
        Assert.InRange(zValues[1], 2.5f, 3.5f);  // first layer touch-down at BASE Z ≈ 0
    }

    [Fact]
    public void Import_uses_print_bed_surface_not_kuka_base_z()
    {
        // LFAM 1: visual plate Z=70, KUKA BASE Z = robroot 500 + base 278 = 778.
        var robroot = new Vector3(0, 0, 500);
        var baseData = new Vector3(1475.5131f, -609.29846f, 278f);
        const float bedZ = 70f;

        var off = KrlExporter.ImportWorldOffset(robroot, baseData, bedZ);
        Assert.Equal(1475.5131f, off.X, 3);
        Assert.Equal(-609.29846f, off.Y, 3);
        Assert.Equal(70f, off.Z, 3); // print bed, not 778

        // Round-trip: a first-layer world point exports to KRL Z≈3 and re-imports to the plate.
        var world = new Vector3(1500f, 900f, 73f);
        var krl = KrlExporter.WorldToBase(world, robroot, baseData, bedZ);
        Assert.InRange(krl.Z, 2.5f, 3.5f);
        var back = KrlExporter.BaseToWorld(krl, robroot, baseData, bedZ);
        Assert.Equal(world.X, back.X, 3);
        Assert.Equal(world.Y, back.Y, 3);
        Assert.Equal(world.Z, back.Z, 3);
    }

    [Fact]
    public void Import_lfam3_bed_matches_robroot_plus_base()
    {
        var robroot = new Vector3(0, 0, 1000);
        var baseData = new Vector3(2135.45f, -52.54f, -83.69f);
        const float bedZ = 916.31f;
        var off = KrlExporter.ImportWorldOffset(robroot, baseData, bedZ);
        Assert.Equal(2135.45f, off.X, 2);
        Assert.Equal(-52.54f, off.Y, 2);
        Assert.Equal(916.31f, off.Z, 2);
    }

    [Fact]
    public void Export_lfam1_home_ptp_includes_e1_rail_position()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 3), new Vector3(10, 0, 3), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var settings = new KrlExportSettings
        {
            ProgramName    = "rail_home",
            HomeE1Mm       = -1100.52f,
        };

        var krl = KrlExporter.Export(tp, settings);
        Assert.Contains("PTP {A1 0.000, A2 -90.000, A3 90.000, A4 0.000, A5 15.000, A6 0.000, E1 -1100.520}", krl);
    }

    [Fact]
    public void Export_travel_and_wipe_always_set_anout4_to_zero()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(60, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            IsWipe = true,
            WipeRpmScale = 1f, // legacy scale — must still force ANOUT 4 = 0
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(60, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(150, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName         = "wipe_travel_off",
            ExtrusionRpmPercent = 50f,
        });

        int wipeIdx = krl.IndexOf(";wipe", StringComparison.Ordinal);
        int travelIdx = krl.IndexOf(";travel", StringComparison.Ordinal);
        Assert.True(wipeIdx >= 0);
        Assert.True(travelIdx >= 0);

        // Immediately after ;wipe / ;travel comments: $ANOUT[4] = 0.000
        Assert.Contains("$ANOUT[4] = 0.000 ; extruder off (wipe)", krl);
        Assert.Contains("$ANOUT[4] = 0.000 ; extruder off", krl);

        // Must not write extrusion RPM for the wipe segment.
        var wipeBlock = krl.Substring(wipeIdx, Math.Min(200, krl.Length - wipeIdx));
        Assert.DoesNotContain("wipe)", wipeBlock.Replace("extruder off (wipe)", ""));
        Assert.DoesNotContain("= 0.5 ; wipe", wipeBlock);
    }

    [Fact]
    public void Urm_honors_edited_header_and_footer_but_falls_back_if_not_urm()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude) { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(150, 0, 10), MoveKind.Extrude) { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        // Gear menu edited a URM-shaped header/footer (e.g. tuned $ADVANCE) — must flow through.
        var editedHeader = KrlExporter.DefaultUrmHeaderTemplate.Replace("$ADVANCE=5", "$ADVANCE=3");
        var editedFooter = KrlExporter.DefaultUrmFooterTemplate.Replace("WAIT SEC 2", "WAIT SEC 4");
        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName = "test_edit", ExtrusionRpmPercent = 50f,
            Temperature1 = 250f, Temperature2 = 250f, Temperature3 = 250f,
            DigitalStartStopEnabled = true,
            HeaderTemplate = editedHeader, FooterTemplate = editedFooter,
        });
        Assert.Contains("$ADVANCE=3", krl);
        Assert.Contains("WAIT SEC 4", krl);
        Assert.Contains(";FOLD CaracolSafety", krl);

        // A stale LFAM (ANOUT) header while URM is on must fall back to the URM default.
        var krl2 = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName = "test_fallback", ExtrusionRpmPercent = 50f,
            Temperature1 = 250f, Temperature2 = 250f, Temperature3 = 250f,
            DigitalStartStopEnabled = true,
            HeaderTemplate = KrlExporter.DefaultHeaderTemplate,
        });
        Assert.DoesNotContain("$ANOUT[1]", krl2);
        Assert.Contains(";FOLD CaracolSafety", krl2);
    }

    [Fact]
    public void RobotMode_exports_settings_menu_Safety_header_not_stock_CaracolSafety()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        const string shopHeader = """
            &ACCESS RVP
            DEF {{PROGRAM_NAME}}()

            ;FOLD Safety
            INTERRUPT DECL 1 WHEN $STOPMESS==TRUE DO STOPEXTRHF()
            INTERRUPT ON 1
            ;in 5 -> Alarms
            ;in 6 -> PLC signal - extrusion enabled
            ;in 7 -> Anticollision Flange
            $CYCFLAG[2] = ($IN[5]==TRUE) OR ($IN[6]==FALSE) OR ($IN[7] == TRUE)
            INTERRUPT DECL 4 WHEN $CYCFLAG[2] DO FULLREMOTESTOPHF()
            INTERRUPT ON 4
            ;ENDFOLD(Safety)

            ;FOLD MAT out of INI
            T1 = {{TEMP1_C}}
            T2 = {{TEMP2_C}}
            T3 = {{TEMP3_C}}
            RPM = 1
            ;ENDFOLD MAT
            """;

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName = "test_shop_header",
            ExtrusionRpmPercent = 50f,
            Temperature1 = 250f,
            RobotModeEnabled = true,
            TravelStartStopEnabled = true,
            HeaderTemplate = shopHeader,
        });

        Assert.Contains(";FOLD Safety", krl);
        Assert.Contains(";in 5 -> Alarms", krl);
        Assert.Contains(";in 7 -> Anticollision Flange", krl);
        Assert.Contains(";ENDFOLD(Safety)", krl);
        Assert.DoesNotContain(";FOLD CaracolSafety", krl);
        Assert.DoesNotContain("Antincendio", krl);
        Assert.DoesNotContain("flangia anti caduta", krl);
        Assert.DoesNotContain("$ANOUT[1]", krl);
    }

    [Fact]
    public void Export_resume_prime_reduces_rpm_during_resume_wait()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(100, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(150, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName             = "test_prime",
            ExtrusionRpmPercent     = 50f,
            ExtrusionResumeWaitSec  = 0.5f,
            SsPreTravelWaitSec      = 0.5f,
            SsResumePrimePercent    = 40f,
            DigitalStartStopEnabled = true,
            HeaderTemplate          = KrlExporter.DefaultHeaderTemplate,
            FooterTemplate          = KrlExporter.DefaultFooterTemplate,
        });

        // Travel Moves writes RPM = 0 / RPM = print. No RCE inject.
        Assert.DoesNotContain("; !Modified by RCE!", krl);
        int travelStart = krl.IndexOf(";travel start", StringComparison.Ordinal);
        int endTr   = krl.IndexOf(";travel end", travelStart, StringComparison.Ordinal);
        int rpmOff  = krl.IndexOf("RPM = 0.00", travelStart, StringComparison.Ordinal);
        int rpmOn   = krl.IndexOf("RPM = 50", travelStart, StringComparison.Ordinal);
        Assert.True(travelStart >= 0 && endTr > travelStart);
        Assert.True(rpmOff > travelStart && rpmOff < endTr);
        Assert.True(rpmOn > rpmOff && rpmOn < endTr);
    }

    static Toolpath TwoMoveTp()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);
        return tp;
    }

    [Fact]
    public void Rotary_cell_export_emits_EK_base_and_E_axis_home()
    {
        var krl = KrlExporter.Export(TwoMoveTp(), new KrlExportSettings
        {
            ProgramName             = "rotary_prog",
            RotaryExternalKinematic = true,
            RotaryMachineDefIndex   = 2,
            BaseDataIndex           = 1,
            HeaderTemplate          = KrlExporter.DefaultHeaderTemplate,
            FooterTemplate          = KrlExporter.DefaultFooterTemplate,
        });
        // External-kinematic base coupling present, indexed to the positioner + base.
        Assert.Contains("$BASE = EK(MACHINE_DEF[2].ROOT,MACHINE_DEF[2].MECH_TYPE,BASE_DATA[1]", krl);
        // Home PTP carries the positioner axes so the first coordinated move is valid.
        Assert.Contains("E1 0.000, E2 0.000, E3 0.000}", krl);
        // EK line sits before the first motion.
        Assert.True(krl.IndexOf("EK(", System.StringComparison.Ordinal)
                    < krl.IndexOf("BAS(#VEL_PTP", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Static_cell_export_has_no_EK_base()
    {
        var krl = KrlExporter.Export(TwoMoveTp(), new KrlExportSettings
        {
            ProgramName             = "static_prog",
            RotaryExternalKinematic = false,
            HeaderTemplate          = KrlExporter.DefaultHeaderTemplate,
            FooterTemplate          = KrlExporter.DefaultFooterTemplate,
        });
        Assert.DoesNotContain("EK(", krl);
        Assert.DoesNotContain("MACHINE_DEF", krl);
    }

    [Fact]
    public void Rotary_cell_injects_EK_into_legacy_header_without_placeholder()
    {
        // A user header saved before {{EK_BASE}} existed — no placeholder, no EK line.
        const string legacyHeader = """
            &ACCESS RVP
            DEF {{PROGRAM_NAME}}()
            BAS(#BASE,{{BASE_NO}})
            BAS(#VEL_PTP,10)
            {{HOME_PTP}}
            """;
        var krl = KrlExporter.Export(TwoMoveTp(), new KrlExportSettings
        {
            ProgramName             = "legacy_rotary",
            RotaryExternalKinematic = true,
            BaseDataIndex           = 1,
            HeaderTemplate          = legacyHeader,
            FooterTemplate          = KrlExporter.DefaultFooterTemplate,
        });
        // Injected right after BAS(#BASE,...) even though the header had no placeholder.
        Assert.Contains("EK(MACHINE_DEF[2]", krl);
        Assert.True(krl.IndexOf("BAS(#BASE", System.StringComparison.Ordinal)
                    < krl.IndexOf("EK(", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Rotary_cell_export_refuses_header_with_no_base_line()
    {
        // Header with neither placeholder nor a BAS(#BASE) anchor to inject after.
        const string brokenHeader = """
            &ACCESS RVP
            DEF {{PROGRAM_NAME}}()
            {{HOME_PTP}}
            """;
        var ex = Assert.Throws<System.InvalidOperationException>(() =>
            KrlExporter.Export(TwoMoveTp(), new KrlExportSettings
            {
                ProgramName             = "broken_rotary",
                RotaryExternalKinematic = true,
                HeaderTemplate          = brokenHeader,
                FooterTemplate          = KrlExporter.DefaultFooterTemplate,
            }));
        Assert.Contains("EK", ex.Message);
    }

    [Fact]
    public void Export_emits_distinct_per_zone_temperatures_not_flattened_to_zone1()
    {
        // Regression: the export call site once passed a single zone-1-derived value for
        // all three KrlExportSettings.Temperature1/2/3, silently flattening a material's
        // distinct zone setpoints (e.g. 290/275/300) to 290/290/290 in the SRC. Each zone
        // must reach the header independently.
        var krl = KrlExporter.Export(TwoMoveTp(), new KrlExportSettings
        {
            ProgramName             = "distinct_zone_temps",
            Temperature1             = 290f,
            Temperature2             = 275f,
            Temperature3             = 300f,
            DigitalStartStopEnabled  = true,
            HeaderTemplate           = KrlExporter.DefaultUrmHeaderTemplate,
            FooterTemplate           = KrlExporter.DefaultFooterTemplate,
        });

        Assert.Contains("T1 = 290", krl);
        Assert.Contains("T2 = 275", krl);
        Assert.Contains("T3 = 300", krl);
        // The re-latch nudge (target - 5) must track each zone's own target, not zone 1's.
        Assert.Contains("T1 = 285", krl);
        Assert.Contains("T2 = 270", krl);
        Assert.Contains("T3 = 295", krl);
    }

    [Fact]
    public void Export_first_layer_speed_and_rpm_override_apply_only_to_layer0()
    {
        // Two layers, one extrude move each. First-layer overrides must hit layer 0
        // only, and speed vs RPM must be independent.
        var tp = new Toolpath();
        var l0 = new ToolpathLayer(0, 3f) { PlaneNormal = Vector3.UnitZ };
        l0.Moves.Add(new ToolpathMove(new Vector3(0,0,3), new Vector3(50,0,3), MoveKind.Extrude){ Normal = Vector3.UnitZ });
        var l1 = new ToolpathLayer(1, 6f) { PlaneNormal = Vector3.UnitZ };
        l1.Moves.Add(new ToolpathMove(new Vector3(0,0,6), new Vector3(50,0,6), MoveKind.Extrude){ Normal = Vector3.UnitZ });
        tp.Layers.Add(l0); tp.Layers.Add(l1);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName          = "first_layer",
            PrintSpeedMps        = 0.100f,   // normal 100 mm/s
            ExtrusionRpmPercent  = 40f,      // normal 40%
            FirstLayerSpeedMps   = 0.040f,   // first layer 40 mm/s
            FirstLayerRpmPercent = 22f,      // first layer 22% (independent of speed)
            DigitalStartStopEnabled = true,
            HeaderTemplate       = KrlExporter.DefaultUrmHeaderTemplate,
            FooterTemplate       = KrlExporter.DefaultFooterTemplate,
        });

        // First-layer speed + RPM present.
        Assert.Contains("$VEL.CP = 0.040000", krl);
        Assert.Contains("RPM = 22", krl);
        // Normal (layer 1) speed + RPM also present, distinct from first layer.
        Assert.Contains("$VEL.CP = 0.100000", krl);
        Assert.Contains("RPM = 40", krl);
        // First-layer values appear before the normal ones (layer 0 emitted first).
        Assert.True(krl.IndexOf("$VEL.CP = 0.040000", System.StringComparison.Ordinal)
                    < krl.IndexOf("$VEL.CP = 0.100000", System.StringComparison.Ordinal));

        // With no overrides (0), the first layer uses the normal speed/RPM (unchanged behavior).
        var krlBase = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName          = "no_first_layer",
            PrintSpeedMps        = 0.100f,
            ExtrusionRpmPercent  = 40f,
            DigitalStartStopEnabled = true,
            HeaderTemplate       = KrlExporter.DefaultUrmHeaderTemplate,
            FooterTemplate       = KrlExporter.DefaultFooterTemplate,
        });
        Assert.DoesNotContain("$VEL.CP = 0.040000", krlBase);
        Assert.DoesNotContain("RPM = 22", krlBase);
    }

    [Fact]
    public void Export_header_includes_version_preset_and_slice_settings()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName        = "meta_print",
            SlicerVersion      = "1234b5  ·  2026-08-15  ·  abc1234",
            MaterialPresetName = "ABS Black",
            MaterialType       = "ABS",
            MaterialColor      = "Black",
            CellName           = "LFAM 3",
            ExtruderIsHf       = false,
            LayerHeightMm      = 3.25f,
            BeadWidthMm        = 6.5f,
            FlowRate           = 0.463f,
            PrintSpeedMps      = 0.1f,
            TravelSpeedMps     = 0.5f,
            WipeSpeedMps       = 0.12f,
            Temperature1       = 230f,
            Temperature2       = 225f,
            Temperature3       = 220f,
            ExtrusionRpmPercent = 50f,
            ToolDataIndex      = 1,
            BaseDataIndex      = 2,
        });

        int def = krl.IndexOf("DEF meta_print", StringComparison.Ordinal);
        int fold = krl.IndexOf(";FOLD MassiveSLICER export", StringComparison.Ordinal);
        Assert.True(def >= 0 && fold > def, "comment block must sit after DEF");
        Assert.Contains("; MassiveSLICER 1234b5  ·  2026-08-15  ·  abc1234", krl);
        Assert.Contains("; Material preset ABS Black", krl);
        Assert.Contains("; Material ABS Black", krl);
        Assert.Contains("; Layer height 3.25 mm", krl);
        Assert.Contains("; Bead width 6.50 mm", krl);
        Assert.Contains("; Extrusion flow 0.4630 rev/cm3", krl);
        Assert.Contains("; Print speed 100.0 mm/s", krl);
        Assert.Contains("; Extrusion RPM 50.0 %", krl);
        Assert.Contains("; T1 230 C  T2 225 C  T3 220 C", krl);
        Assert.Contains("; TOOL 1  BASE 2", krl);
        Assert.Contains(";ENDFOLD (MassiveSLICER export)", krl);
    }

    [Fact]
    public void Export_header_preset_none_when_unset()
    {
        var block = KrlExporter.BuildExportCommentBlock(new KrlExportSettings { ProgramName = "x" });
        Assert.Contains("; Material preset (none)", block);
        Assert.Contains("; MassiveSLICER (unknown)", block);
    }
}
