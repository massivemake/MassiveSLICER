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
            TravelSetAnout4Zero = false,
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
            TravelSetAnout4Zero   = false,
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
            TravelSetAnout4Zero    = true,
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
            TravelSetAnout4Zero = true,
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
            TravelSetAnout4Zero = true,
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
            TravelSetAnout4Zero = true,
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
            TravelSetAnout4Zero = false, // even when flag is off, travel/wipe must be 0
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
}