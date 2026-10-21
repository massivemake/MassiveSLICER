using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

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
    public void Spiral_z_lift_is_a_vertical_hop_not_a_circle()
    {
        const string gcode = """
            G90
            M83
            ; CHANGE_LAYER
            ; Z_HEIGHT: 0.2
            ; LAYER_HEIGHT: 0.2
            G1 X10 Y10 Z0.2
            G1 X20 Y10 E0.4
            ; WIPE_START
            G1 X22 Y10 E-0.1
            ; WIPE_END
            G3 Z0.6 I1.2 J0 P1 F60000
            G1 X30 Y10 Z0.6
            G1 Z0.2
            G1 X40 Y10 E0.4
            """;
        var result = ScaledGcodeImporter.Import(gcode, 100f, Vector3.Zero);
        var moves = result.Toolpath.Layers.SelectMany(l => l.Moves).ToList();
        Assert.True(moves.Count < 8, $"desktop hop expanded into {moves.Count} moves");
        Assert.DoesNotContain(moves, m => m.IsZHop);
        var travel = Assert.Single(moves, m => m.Kind == MoveKind.Travel);
        Assert.Equal(travel.From.Z, travel.To.Z, 3);
        Assert.Contains(moves, m => m.Kind == MoveKind.Extrude);

        var finished = MovementPostProcessor.Apply(result.Toolpath, new SliceSettings
        {
            LayerHeight = result.LayerHeightMm,
            ZHopMm = 5f,
            WipeMode = WipeMode.SameDirection,
            WipeLengthMm = 10f,
            WipeRampMm = 5f,
        });
        var done = finished.Layers.SelectMany(l => l.Moves).ToList();
        Assert.Contains(done, m => m.IsWipe);
        Assert.Contains(done, m => m.IsZHop);
        Assert.Contains(done, m => m.Kind == MoveKind.Extrude && !m.IsWipe);
    }

    [Fact]
    public void Desktop_plate_spiral_lifts_do_not_become_circles()
    {
        var path = Environment.GetEnvironmentVariable("SCALED_GCODE_3MF");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        var result = ScaledGcodeImporter.Import(ScaledGcodeImporter.ReadGcodeText(path), 10f, Vector3.Zero);
        var imported = result.Toolpath.Layers.SelectMany(l => l.Moves).ToList();
        var travels = imported.Where(m => m.Kind == MoveKind.Travel).ToList();
        int extrudes = imported.Count(m => m.Kind == MoveKind.Extrude);
        Assert.True(extrudes > 100_000, $"print moves dropped: {extrudes}");
        Assert.DoesNotContain(imported, m => m.IsZHop);
        int chord = travels.Count(m =>
        {
            float dx = m.To.X - m.From.X;
            float dy = m.To.Y - m.From.Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            return len > 2.2f && len < 2.7f && MathF.Abs(m.To.Z - m.From.Z) < 0.05f;
        });
        Assert.True(chord < 500, $"spiral-lift chords still in the path: {chord} of {travels.Count} travels");

        var finished = MovementPostProcessor.Apply(result.Toolpath, new SliceSettings
        {
            LayerHeight = result.LayerHeightMm,
            ZHopMm = 5f,
            WipeMode = WipeMode.SameDirection,
            WipeLengthMm = 35f,
            WipeRampMm = 5f,
        });
        var done = finished.Layers.SelectMany(l => l.Moves).ToList();
        Assert.Contains(done, m => m.IsWipe);
        Assert.Contains(done, m => m.IsZHop);
        Assert.Equal(extrudes, done.Count(m => m.Kind == MoveKind.Extrude && !m.IsWipe));
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
    public void Purge_chute_before_the_first_layer_is_not_imported()
    {
        const string gcode = """
            G90
            M83
            G1 X20 Y-3 Z0.4 F12000
            ; CHANGE_LAYER
            ; Z_HEIGHT: 0.2
            ; LAYER_HEIGHT: 0.2
            G1 Z0.4
            G1 X10 Y10 Z0.2
            G1 X30 Y10 E0.4
            G1 X30 Y30 E0.4
            """;
        var result = ScaledGcodeImporter.Import(gcode, 10f, Vector3.Zero);
        var pts = result.Toolpath.Layers.SelectMany(l => l.Moves).SelectMany(m => new[] { m.From, m.To });
        Assert.DoesNotContain(pts, p => MathF.Abs(p.Y + 130f) < 5f || p.Y < -200f);
        Assert.Contains(result.Toolpath.Layers.SelectMany(l => l.Moves), m => m.Kind == MoveKind.Extrude);
    }

    [Fact]
    public void Combing_travels_are_not_folded_into_a_chord()
    {
        const string gcode = """
            G90
            M83
            ;LAYER_CHANGE
            ;LINE_WIDTH:0.8
            ;LAYER_HEIGHT:0.3
            G1 X0 Y0 Z0.3
            G1 X10 Y0 E0.2
            G0 X10 Y5
            G0 X5 Y10
            G0 X0 Y10
            G1 X0 Y8 E0.1
            """;
        var result = ScaledGcodeImporter.Import(gcode, 100f, Vector3.Zero);
        var travels = result.Toolpath.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Travel).ToList();
        Assert.True(travels.Count >= 3, "the comb was folded into one chord");
        Assert.Contains(travels, m =>
            MathF.Abs((m.To.X - m.From.X) - (m.From.Y - m.To.Y)) < 0.2f
            && MathF.Abs(m.To.X - m.From.X) > 3f);
    }

    [Fact]
    public void Travel_across_an_opening_follows_the_printed_wall()
    {
        const string gcode = """
            G90
            M82
            ;LAYER_CHANGE
            ;LINE_WIDTH:0.8
            ;LAYER_HEIGHT:0.3
            G1 X0 Y0 Z0.3 F1800 E0.01
            G1 X40 Y0 E0.20
            G1 X40 Y40 E0.40
            G1 X0 Y40 E0.60
            G1 X0 Y0 E0.80
            G0 X40 Y40 Z0.3
            G1 X30 Y40 E0.90
            """;
        var result = ScaledGcodeImporter.Import(gcode, 100f, Vector3.Zero);
        var travels = result.Toolpath.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Travel).ToList();
        Assert.DoesNotContain(travels, m => Vector3.Distance(m.From, m.To) > 50f);
        Assert.Contains(travels, m =>
            MathF.Abs(m.To.Y - m.From.Y) < 0.2f && MathF.Abs(m.To.X - m.From.X) > 20f);
        Assert.Equal(0.8f, result.BeadWidthMm, 2);
        Assert.Equal(0.8f, result.Toolpath.BeadWidthMm, 2);
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
