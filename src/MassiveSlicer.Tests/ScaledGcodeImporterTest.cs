using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class ScaledGcodeImporterTest
{
    const string Sample = """
        ; HEADER_BLOCK_START
        ; max_z_height: 1.00
        ; HEADER_BLOCK_END
        G28
        G1 X0 Y0 F12000
        ; MACHINE_START_GCODE_END
        G90
        M83
        ; CHANGE_LAYER
        ; Z_HEIGHT: 0.2
        ; LAYER_HEIGHT: 0.2
        ; FEATURE: Inner wall
        ; LINE_WIDTH: 0.5
        G1 X10 Y10 Z0.2
        G1 X20 Y10 E0.4 F1200
        G1 X20 Y20 E0.4
        ; CHANGE_LAYER
        ; Z_HEIGHT: 0.4
        ; LAYER_HEIGHT: 0.2
        G1 Z0.6
        G1 X10 Y20 Z0.4
        G1 X10 Y10 E0.4
        ; MACHINE_END_GCODE_START
        G1 X200 Y200 Z50
        """;

    [Fact]
    public void Ten_percent_file_scales_to_full_size_and_drops_the_prime_line()
    {
        var result = ScaledGcodeImporter.Import(Sample, sourcePercent: 10f, bedWorldOffset: new Vector3(1000, 2000, 900));
        Assert.Equal(10f, result.ScaleFactor);
        Assert.Equal(2, result.LayerCount);
        Assert.True(result.MoveCount >= 3);
        Assert.Equal(5f, result.BeadWidthMm, 3);
        Assert.Equal(2f, result.LayerHeightMm, 3);
        Assert.Equal(100f, result.SizeMm.X, 1);
        Assert.Equal(100f, result.SizeMm.Y, 1);

        var extrude = result.Toolpath.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Extrude).ToList();
        Assert.NotEmpty(extrude);
        Assert.DoesNotContain(extrude, m => m.To.X > 1500f);
        Assert.All(extrude, m => Assert.InRange(m.To.Z, 900f, 910f));
    }

    [Fact]
    public void Arc_with_extrusion_is_a_print_not_a_chord_skip()
    {
        const string gcode = """
            G90
            M83
            ; CHANGE_LAYER
            ; Z_HEIGHT: 0.2
            ; LAYER_HEIGHT: 0.2
            G1 X0 Y0 Z0.2
            G2 X10 Y0 I5 J0 E1.2 F1800
            """;
        var result = ScaledGcodeImporter.Import(gcode, 100f, Vector3.Zero);
        var cuts = result.Toolpath.Layers.SelectMany(l => l.Moves).Count(m => m.Kind == MoveKind.Extrude);
        Assert.True(cuts >= 4);
    }

    [Fact]
    public void Wipe_block_is_not_printed()
    {
        const string gcode = """
            G90
            M83
            ; CHANGE_LAYER
            G1 X0 Y0 Z0.2
            G1 X10 Y0 E0.2
            ; WIPE_START
            G1 X12 Y0 E0.5
            ; WIPE_END
            G1 X10 Y10 E0.2
            """;
        var result = ScaledGcodeImporter.Import(gcode, 100f, Vector3.Zero);
        var cuts = result.Toolpath.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Extrude).ToList();
        Assert.Equal(2, cuts.Count);
        Assert.DoesNotContain(cuts, m => MathF.Abs(m.To.X - 12f) < 0.1f && MathF.Abs(m.To.Y) < 0.1f);
    }

    [Fact]
    public void Real_gcode_3mf_scales_back_to_full_size_when_present()
    {
        var path = Environment.GetEnvironmentVariable("SCALED_GCODE_3MF");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        var text = ScaledGcodeImporter.ReadGcodeText(path);
        var result = ScaledGcodeImporter.Import(text, 10f, Vector3.Zero);
        Assert.True(result.MoveCount > 1000, $"moves={result.MoveCount} layers={result.LayerCount} size={result.SizeMm}");
        Assert.True(result.LayerCount > 100);
        Assert.InRange(result.SizeMm.Z, 800f, 1200f);
        Assert.InRange(result.SizeMm.X, 50f, 2500f);
        Assert.Contains(result.Toolpath.Layers.SelectMany(l => l.Moves), m => m.Kind == MoveKind.Extrude);
    }
}
