using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests;

public class MaterialCalibrationKrlTest
{
    [Fact]
    public void Generate_IsPointLoaderCompatible_AbsoluteLinAndPtpOnly()
    {
        var s = new MaterialCalibrationKrl.Settings
        {
            ProgramName   = "MatCal_Test",
            MaterialName  = "ABS - Black",
            Temperature1  = 250,
            Temperature2  = 250,
            Temperature3  = 240,
            MotorPercent  = 50,
            RunTimeSec    = 60,
            PurgeHeightMm = 304.8f,
            HomePosition  = [0, -90, 90, 0, 15, 0],
            HomeE1Mm      = -1000,
            ToolDataIndex = 1,
            BaseDataIndex = 1,
            HomeTcpBase   = new MaterialCalibrationKrl.CartesianPose(100, 200, 800, 0, 90, 0),
        };

        string krl = MaterialCalibrationKrl.Generate(s);

        Assert.Contains("DEF MatCal_Test ( )", krl);
        // PointLoader requires full pose aggregates — no variable targets
        Assert.DoesNotContain("purgePos", krl);
        Assert.DoesNotContain("$POS_ACT", krl);
        Assert.DoesNotContain("DECL E6POS", krl);
        Assert.Contains("PTP {A1 0.000", krl);
        Assert.Contains("E1 -1000.000", krl);
        Assert.Contains("LIN {X 100.00, Y 200.00, Z 304.80", krl);
        Assert.Contains("Z 354.80", krl); // retract = 304.8 + 50
        Assert.Contains("$ANOUT[4] = 0.5", krl);
        Assert.Contains("WAIT SEC 60", krl);
        Assert.Contains("$ANOUT[4] = 0.000", krl);
        Assert.Contains("WAIT FOR $IN[6]==TRUE", krl);
        Assert.Contains(KrlAnout.TempToAnoutText(250), krl);
        Assert.Contains("PointLoader", krl);
    }

    [Fact]
    public void FromPreset_EstimatesHomeTcpAndEmitsLin()
    {
        var cell = CellLoader.Load("assets/cells/LFAM1/lfam1.json");
        Assert.NotNull(cell);

        var preset = new MaterialPreset
        {
            Name            = "ASA - Gray",
            Temperature1    = 230,
            Temperature2    = 230,
            Temperature3    = 220,
            MaterialDensity = 1.07,
        };

        var settings = MaterialCalibrationKrl.FromPreset(
            preset, motorPercent: 40, runTimeSec: 45, cell: cell, homeE1Mm: -500);

        Assert.Equal(MaterialCalibrationKrl.SuggestProgramName("ASA - Gray"), settings.ProgramName);
        Assert.Equal(40f, settings.MotorPercent);
        Assert.NotNull(settings.HomeTcpBase);

        string krl = MaterialCalibrationKrl.Generate(settings);
        Assert.Contains("WAIT SEC 45", krl);
        Assert.Contains(KrlAnout.RpmPercentToAnoutText(40), krl);
        Assert.Contains("LIN {X ", krl);
        Assert.Contains($"Z {MaterialCalibrationKrl.DefaultPurgeHeightMm.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}", krl);
        Assert.DoesNotContain("$POS_ACT", krl);
    }

    [Fact]
    public void SuggestProgramName_Sanitizes()
    {
        Assert.StartsWith("MatCal_", MaterialCalibrationKrl.SuggestProgramName("ABS - Black"));
        Assert.DoesNotContain(" ", MaterialCalibrationKrl.SuggestProgramName("ABS - Black"));
        Assert.DoesNotContain("__", MaterialCalibrationKrl.SuggestProgramName("ASA - Black"));
    }
}
