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
        // Travel Moves: RPM = 0 at ;travel start. One RPM = after ;travel end. No RCE inject.
        Assert.Contains(";travel start", krl);
        Assert.Contains(";travel end", krl);
        Assert.DoesNotContain("; !Modified by RCE!", krl);
        Assert.DoesNotContain("TRIGGER WHEN DISTANCE", krl);
        Assert.DoesNotContain("$OUT[7] = TRUE; !Modified by RCE!", krl);

        int travel = krl.IndexOf(";travel start", StringComparison.Ordinal);
        int syncOff = krl.IndexOf("WAIT SEC 0", travel, StringComparison.Ordinal);
        int rpmOff = krl.IndexOf("RPM = 0.00", travel, StringComparison.Ordinal);
        int travelEnd = krl.IndexOf(";travel end", travel, StringComparison.Ordinal);
        int rpmOn  = krl.IndexOf("RPM = 50", travelEnd, StringComparison.Ordinal);
        int syncOn = krl.LastIndexOf("WAIT SEC 0", rpmOn, StringComparison.Ordinal);
        Assert.True(travel >= 0 && syncOff > travel && rpmOff > syncOff && travelEnd > rpmOff);
        Assert.True(syncOn > travelEnd && rpmOn > syncOn);
        AssertNoDuplicateTravelResumeRpm(krl);
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
        // Resume in LFAM analog mode is $ANOUT[4], not a second RPM = line.
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

        var motionZ = Regex.Matches(krl, @"(?:PTP|LIN) \{[^}]*Z (-?\d+\.\d+)")
            .Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Take(2)
            .ToArray();

        Assert.Equal(2, motionZ.Length);
        Assert.InRange(motionZ[0], 52f, 54f);   // approach PTP: layer Z 3 + 50 mm
        Assert.InRange(motionZ[1], 2.5f, 3.5f);  // first layer touch-down at BASE Z ≈ 0
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
    public void Export_first_approach_is_cartesian_ptp_50mm_above_first_lin()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0.5f) { PlaneNormal = new Vector3(0, 0.7071068f, 0.7071068f) };
        layer.Moves.Add(new ToolpathMove(
            new Vector3(-101.13f, -451.46f, 0.5f),
            new Vector3(-90f, -451.46f, 0.5f),
            MoveKind.Extrude) { Normal = layer.PlaneNormal });
        tp.Layers.Add(layer);

        var home = new float[] { -0.220f, -79.080f, 115.260f, 179.690f, 22.580f, -179.830f };
        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName             = "approach_ptp",
            ApproachZMm             = 50f,
            RotaryExternalKinematic = true,
            RotaryMachineDefIndex   = 2,
            BaseDataIndex           = 1,
            HomePosition            = home,
            // Viewport IK joints must not win — they converted to Z through the bed.
            ApproachJoints          = [5.020f, -22.698f, 92.743f, 185.693f, 70.064f, -137.272f],
        });

        Assert.Contains(";approach", krl);
        Assert.DoesNotContain("PTP {A1 5.020", krl);
        Assert.Contains("PTP {X -101.13, Y -451.46, Z 50.50", krl);
        Assert.Contains("S 4, T 35", krl);
        Assert.Contains("LIN {X -101.13, Y -451.46, Z 0.50", krl);
    }

    [Fact]
    public void Export_first_approach_cartesian_ptp_uses_home_status_turn()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0.5f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(
            new Vector3(-101.13f, -451.46f, 0.5f),
            new Vector3(-90f, -451.46f, 0.5f),
            MoveKind.Extrude) { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName = "approach_lin",
            ApproachZMm = 50f,
        });

        Assert.Contains("PTP {X -101.13, Y -451.46, Z 50.50", krl);
        Assert.Contains("S 4, T 2", krl); // default home A2=-90, A5=15
        Assert.DoesNotContain("LIN {X -101.13, Y -451.46, Z 50.50", krl);
    }

    [Fact]
    public void Approach_ik_target_uses_lfam1_rail_e1_not_cell_origin()
    {
        // Dragon Column: E1 -939.6, rail Y e1Sign -1 → ROBROOT +939.6 mm in Y.
        var s = new KrlExportSettings
        {
            ProgramName    = "rail_ik",
            HomeE1Mm       = -939.6f,
            RailAxis       = "Y",
            RailE1Sign     = -1f,
            RailMinMm      = -4650f,
            RailMaxMm      = 150f,
            RobrootWorldPos = new Vector3(0f, 0f, 500f),
        };
        var world = new Vector3(1390.50f, 52.35f, 552f);
        var rel = KrlExporter.ApproachIkTargetRobroot(world, s);
        var naive = world - s.RobrootWorldPos;
        Assert.Equal(naive.X, rel.X, 2);
        Assert.Equal(naive.Z, rel.Z, 2);
        // SceneOffset Y = (-1) * (-939.6) = +939.6
        Assert.Equal(naive.Y - 939.6f, rel.Y, 1);
        Assert.NotEqual(naive.Y, rel.Y);
    }

    [Fact]
    public void Approach_ik_target_ignores_rail_on_rotary()
    {
        var s = new KrlExportSettings
        {
            ProgramName             = "rot_ik",
            HomeE1Mm                = -12.5f,
            RotaryExternalKinematic = true,
            RobrootWorldPos         = new Vector3(0f, 0f, 1000f),
        };
        var world = new Vector3(100f, 200f, 1100f);
        var rel = KrlExporter.ApproachIkTargetRobroot(world, s);
        Assert.Equal(world - s.RobrootWorldPos, rel);
    }

    [Fact]
    public void Export_uses_selected_home_joints_as_start_ptp()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 3), new Vector3(10, 0, 3), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName             = "home_pick",
            HomePosition            = [0f, -85f, 90f, 15f, 0f, 0f, -12.5f],
            RotaryExternalKinematic = true,
            RotaryMachineDefIndex   = 2,
            BaseDataIndex           = 1,
        });

        Assert.Contains(
            "PTP {A1 0.000, A2 -85.000, A3 90.000, A4 15.000, A5 0.000, A6 0.000, E1 -12.500, E2 0.000, E3 0.000}",
            krl);
        Assert.DoesNotContain("A2 -90.000", krl);
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
    public void Export_collapses_wipe_comments_and_same_vel_cp()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        for (int i = 0; i < 5; i++)
        {
            float x0 = 50 - i * 1.25f;
            layer.Moves.Add(new ToolpathMove(new Vector3(x0, 0, 10), new Vector3(x0 - 1.25f, 0, 10), MoveKind.Extrude)
            {
                Normal = Vector3.UnitZ,
                IsWipe = true,
            });
        }
        layer.Moves.Add(new ToolpathMove(new Vector3(43.75f, 0, 10), new Vector3(43.75f, 0, 15), MoveKind.Travel)
            { IsZHop = true, TravelSpeedMps = 0.6f });
        layer.Moves.Add(new ToolpathMove(new Vector3(43.75f, 0, 15), new Vector3(-50, 0, 15), MoveKind.Travel)
            { IsZHop = true, TravelSpeedMps = 0.6f });
        layer.Moves.Add(new ToolpathMove(new Vector3(-50, 0, 15), new Vector3(-50, 0, 10), MoveKind.Travel)
            { IsZHop = true, TravelSpeedMps = 0.6f });
        layer.Moves.Add(new ToolpathMove(new Vector3(-50, 0, 10), new Vector3(-10, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName            = "wipe_dedupe",
            ExtrusionRpmPercent    = 23.64426f,
            PrintSpeedMps          = 0.04f,
            TravelSpeedMps         = 0.6f,
            WipeSpeedMps           = 0.6f,
            RobotModeEnabled       = true,
            TravelStartStopEnabled = true,
        });

        int wipeCount = CountOccurrences(krl, ";wipe");
        int zHopCount = CountOccurrences(krl, ";z-hop");
        Assert.Equal(1, wipeCount);
        Assert.Equal(1, zHopCount);
        int wipeIdx = krl.IndexOf(";wipe", StringComparison.Ordinal);
        int endIdx  = krl.IndexOf(";travel end", wipeIdx, StringComparison.Ordinal);
        Assert.True(endIdx > wipeIdx);
        var hopBlock = krl.Substring(wipeIdx, endIdx - wipeIdx);
        Assert.DoesNotContain("$VEL.CP", hopBlock);
        Assert.Contains("$VEL.CP = 0.040000", krl);
        Assert.Contains("LIN {X 48.75", krl);
        Assert.Contains("LIN {X 43.75", krl);
        int firstLin = hopBlock.IndexOf("LIN {", StringComparison.Ordinal);
        Assert.True(firstLin >= 0);
        int firstNl = hopBlock.IndexOf('\n', firstLin);
        var firstWipeLin = firstNl > firstLin ? hopBlock.Substring(firstLin, firstNl - firstLin) : hopBlock[firstLin..];
        Assert.Contains("E2 1.000", firstWipeLin);
        Assert.DoesNotContain("E2 1.000", hopBlock.Substring(firstNl));
    }

    [Fact]
    public void Export_rotary_cell_still_flags_e2_on_first_wipe()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(48, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ, IsWipe = true });
        layer.Moves.Add(new ToolpathMove(new Vector3(48, 0, 10), new Vector3(46, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ, IsWipe = true });
        tp.Layers.Add(layer);
        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName = "rotary_wipe",
            ExtrusionRpmPercent = 20f,
            PrintSpeedMps = 0.04f,
            TravelStartStopEnabled = true,
            RotaryExternalKinematic = true,
        });
        int wipeIdx = krl.IndexOf(";wipe", StringComparison.Ordinal);
        Assert.True(wipeIdx >= 0);
        int firstLin = krl.IndexOf("LIN {", wipeIdx, StringComparison.Ordinal);
        int firstNl = krl.IndexOf('\n', firstLin);
        var firstWipeLin = krl.Substring(firstLin, firstNl - firstLin);
        Assert.Contains("E2 1.000", firstWipeLin);
        int secondLin = krl.IndexOf("LIN {", firstNl, StringComparison.Ordinal);
        int secondNl = krl.IndexOf('\n', secondLin);
        Assert.Contains("E2 0.000", krl.Substring(secondLin, secondNl - secondLin));
    }

    [Fact]
    public void Export_smash_wipe_is_exact_stop_with_e2_flag()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 10), new Vector3(50, 0, 9), MoveKind.Extrude)
            { Normal = Vector3.UnitZ, IsWipe = true, WipeRpmScale = 0f });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 0, 9), new Vector3(85, 0, 9), MoveKind.Extrude)
            { Normal = Vector3.UnitZ, IsWipe = true, WipeRpmScale = 1f });
        tp.Layers.Add(layer);
        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName = "smash_wipe",
            ExtrusionRpmPercent = 20f,
            PrintSpeedMps = 0.04f,
            TravelStartStopEnabled = true,
        });
        int wipeIdx = krl.IndexOf(";wipe", StringComparison.Ordinal);
        Assert.True(wipeIdx >= 0);
        Assert.Contains("T1 = 180", krl);
        int dipIdx = krl.IndexOf("T1 = 180", wipeIdx, StringComparison.Ordinal);
        Assert.True(dipIdx > wipeIdx);
        int restoreIdx = krl.IndexOf("T1 = 230", dipIdx, StringComparison.Ordinal);
        Assert.True(restoreIdx > dipIdx);
        int firstLin = krl.IndexOf("LIN {", wipeIdx, StringComparison.Ordinal);
        int firstNl = krl.IndexOf('\n', firstLin);
        var smashLin = krl.Substring(firstLin, firstNl - firstLin);
        Assert.Contains("Z 9.00", smashLin);
        Assert.Contains("E2 1.000", smashLin);
        Assert.DoesNotContain("C_VEL", smashLin);
        int secondLin = krl.IndexOf("LIN {", firstNl, StringComparison.Ordinal);
        int secondNl = krl.IndexOf('\n', secondLin);
        var wipeLin = krl.Substring(secondLin, secondNl - secondLin);
        Assert.Contains("C_VEL", wipeLin);
        Assert.Contains("E2 0.000", wipeLin);
    }

    static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
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

        // Travel Moves writes RPM = 0 at start, one RPM = after ;travel end. No RCE inject.
        Assert.DoesNotContain("; !Modified by RCE!", krl);
        int travelStart = krl.IndexOf(";travel start", StringComparison.Ordinal);
        int endTr   = krl.IndexOf(";travel end", travelStart, StringComparison.Ordinal);
        int rpmOff  = krl.IndexOf("RPM = 0.00", travelStart, StringComparison.Ordinal);
        int rpmOn   = krl.IndexOf("RPM = 50", endTr, StringComparison.Ordinal);
        Assert.True(travelStart >= 0 && endTr > travelStart);
        Assert.True(rpmOff > travelStart && rpmOff < endTr);
        Assert.True(rpmOn > endTr);
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
            WorkspaceFileName  = "2026_0819_Cow_Capital_Bottom02.mass",
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
        Assert.Contains("; Cell LFAM 3", krl);
        Assert.Contains("; Workspace 2026_0819_Cow_Capital_Bottom02.mass", krl);
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
        Assert.DoesNotContain("; Workspace ", block);
    }

    [Fact]
    public void MassWorkspaceFileName_is_filename_only_and_only_for_mass()
    {
        Assert.Equal("job.mass", KrlExporter.MassWorkspaceFileName(@"Z:\Projects\job.mass"));
        Assert.Equal("job.mass", KrlExporter.MassWorkspaceFileName("/Volumes/share/Cow Bottom.mass"));
        Assert.Null(KrlExporter.MassWorkspaceFileName(@"Z:\Projects\job.src"));
        Assert.Null(KrlExporter.MassWorkspaceFileName(null));
        Assert.Null(KrlExporter.MassWorkspaceFileName(""));
    }

    static void AssertNoDuplicateTravelResumeRpm(string krl)
    {
        var lines = krl.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(";travel end", StringComparison.Ordinal))
                continue;
            string? before = null;
            for (int j = i - 1; j >= 0; j--)
            {
                if (string.IsNullOrWhiteSpace(lines[j])) continue;
                before = lines[j].Trim();
                break;
            }
            string? after = null;
            for (int j = i + 1; j < lines.Length; j++)
            {
                if (string.IsNullOrWhiteSpace(lines[j])) continue;
                after = lines[j].Trim();
                break;
            }
            if (before is null || after is null) continue;
            static string RpmCore(string line)
            {
                if (!line.StartsWith("RPM =", StringComparison.Ordinal)) return "";
                int semi = line.IndexOf(';');
                return (semi >= 0 ? line[..semi] : line).Trim();
            }
            var a = RpmCore(before);
            var b = RpmCore(after);
            Assert.False(a.Length > 0 && a == b,
                $"duplicate RPM around ;travel end: '{before}' / '{after}'");
        }
    }
}
