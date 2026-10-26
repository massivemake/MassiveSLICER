using System.Globalization;
using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class TimelineTrimTest
{
    [Fact]
    public void Extract_keeps_world_points_layer_index_and_leaves_the_source()
    {
        var source = Path(
            Layer(4, 12f, Vector3.UnitZ,
                Move(new Vector3(0, 0, 12), new Vector3(10, 0, 12), 11f),
                Move(new Vector3(10, 0, 12), new Vector3(20, 1, 12), 22f),
                Move(new Vector3(20, 1, 12), new Vector3(30, 5, 12), 33f),
                Move(new Vector3(30, 5, 12), new Vector3(40, 5, 12), 44f)));
        source.Layers[0].Contours.Add(new ContourSpan(0, 4, true, -1));
        source.BeadWidthMm = 6.5f;

        var excerpt = TimelineTrim.Extract(source, 1, 3);

        var layer = Assert.Single(excerpt.Layers);
        Assert.Equal(4, layer.Index);
        Assert.Equal(12f, layer.Z);
        Assert.Equal(3f, layer.Height);
        Assert.Equal(Vector3.UnitZ, layer.PlaneNormal);
        Assert.Empty(layer.Contours);
        Assert.Equal(6.5f, excerpt.BeadWidthMm);
        Assert.Equal(2, layer.Moves.Count);

        Assert.Equal(new Vector3(10, 0, 12), layer.Moves[0].From);
        Assert.Equal(new Vector3(20, 1, 12), layer.Moves[0].To);
        Assert.Equal(22f, layer.Moves[0].E1Mm);
        Assert.Equal(7f, layer.Moves[0].KeyframeToolheadA);
        Assert.Equal(new Vector3(30, 5, 12), layer.Moves[1].To);
        Assert.Equal(33f, layer.Moves[1].E1Mm);

        layer.Moves[0].E1Mm = 999f;
        Assert.Equal(22f, source.Layers[0].Moves[1].E1Mm);
        Assert.Equal(4, source.Layers[0].Moves.Count);
    }

    [Fact]
    public void Extract_splits_across_layers_and_keeps_each_index()
    {
        var source = Path(
            Layer(2, 6f, Vector3.UnitZ,
                Move(new Vector3(0, 0, 6), new Vector3(10, 0, 6), 1f),
                Move(new Vector3(10, 0, 6), new Vector3(20, 0, 6), 2f)),
            Layer(5, 9f, Vector3.UnitY,
                Move(new Vector3(20, 0, 9), new Vector3(30, 0, 9), 3f),
                Move(new Vector3(30, 0, 9), new Vector3(40, 0, 9), 4f)));

        var excerpt = TimelineTrim.Extract(source, 1, 3);

        Assert.Equal(2, excerpt.Layers.Count);
        Assert.Equal(2, excerpt.Layers[0].Index);
        Assert.Equal(5, excerpt.Layers[1].Index);
        Assert.Equal(Vector3.UnitY, excerpt.Layers[1].PlaneNormal);
        Assert.Equal(new Vector3(10, 0, 6), excerpt.Layers[0].Moves[0].From);
        Assert.Equal(new Vector3(30, 0, 9), excerpt.Layers[1].Moves[0].To);
    }

    [Fact]
    public void Extract_rejects_an_empty_or_travel_only_span()
    {
        var source = Path(Layer(1, 3f, Vector3.UnitZ,
            new ToolpathMove(new Vector3(0, 0, 3), new Vector3(5, 0, 3), MoveKind.Travel),
            Move(new Vector3(5, 0, 3), new Vector3(15, 0, 3), 1f)));

        var empty = Assert.Throws<InvalidOperationException>(() => TimelineTrim.Extract(source, 2, 2));
        Assert.Contains("empty", empty.Message, StringComparison.OrdinalIgnoreCase);

        var travel = Assert.Throws<InvalidOperationException>(() => TimelineTrim.Extract(source, 0, 1));
        Assert.Contains("no print or mill", travel.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Program_suffix_stays_on_a_long_stem_and_is_not_doubled()
    {
        Assert.Equal("_m100-200", TimelineTrim.ProgramSuffix(100, 200));
        var once = TimelineTrim.AppendProgramSuffix(new string('A', 70), 10, 20, 64);
        Assert.EndsWith("_m10-20", once, StringComparison.Ordinal);
        Assert.True(once.Length <= 64);
        Assert.Equal(once, TimelineTrim.AppendProgramSuffix(once, 10, 20, 64));
    }

    [Fact]
    public void Exported_section_matches_the_full_program_lin_and_skips_first_layer_speed()
    {
        var source = Path(Layer(4, 12f, Vector3.UnitZ,
            Move(new Vector3(0, 0, 12), new Vector3(10, 0, 12), 100.5f),
            Move(new Vector3(10, 0, 12), new Vector3(20, 1, 12), 200.25f),
            Move(new Vector3(20, 1, 12), new Vector3(30, 5, 12), 310f),
            Move(new Vector3(30, 5, 12), new Vector3(40, 5, 12), 400f)));
        var settings = PrintSettings();
        var excerpt = TimelineTrim.Extract(source, 1, 3);

        var full = KrlExporter.Export(source, settings);
        var section = KrlExporter.Export(excerpt, settings);

        var point = KrlExporter.WorldToBase(
            new Vector3(30, 5, 12), settings.RobrootWorldPos, settings.BaseDataOffset, settings.SliceBedWorldZ);
        var fullLin = LinAt(full, point);
        var sectionLin = LinAt(section, point);
        Assert.Equal(fullLin, sectionLin);
        Assert.Contains("E1 310.000", sectionLin, StringComparison.Ordinal);
        Assert.Contains("C_VEL", sectionLin, StringComparison.Ordinal);

        Assert.Contains("$VEL.CP = 0.100000", section, StringComparison.Ordinal);
        Assert.DoesNotContain("$VEL.CP = 0.040000", section, StringComparison.Ordinal);
        Assert.DoesNotContain("RPM = 22", section, StringComparison.Ordinal);
        Assert.Contains("RPM = 40", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Exported_section_of_layer_zero_still_uses_the_first_layer_override()
    {
        var source = Path(
            Layer(0, 3f, Vector3.UnitZ, Move(new Vector3(0, 0, 3), new Vector3(10, 0, 3), 1f)),
            Layer(1, 6f, Vector3.UnitZ, Move(new Vector3(10, 0, 6), new Vector3(20, 0, 6), 2f)));
        var section = KrlExporter.Export(TimelineTrim.Extract(source, 0, 1), PrintSettings());

        Assert.Contains("$VEL.CP = 0.040000", section, StringComparison.Ordinal);
        Assert.Contains("RPM = 22", section, StringComparison.Ordinal);
        Assert.DoesNotContain("$VEL.CP = 0.100000", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Mill_section_cut_matches_the_full_program_point()
    {
        var source = Path(Layer(2, 8f, Vector3.UnitZ,
            Mill(new Vector3(0, 0, 8), new Vector3(10, 0, 8), 50f),
            Mill(new Vector3(10, 0, 8), new Vector3(20, 4, 8), 80f),
            Mill(new Vector3(20, 4, 8), new Vector3(30, 4, 8), 90f)));
        var settings = new KrlExportSettings
        {
            ProgramName = "mill_section",
            IsMilling = true,
            ApproachZMm = 15f,
            RobrootWorldPos = new Vector3(100, 200, 0),
            BaseDataOffset = new Vector3(5, 6, 7),
        };
        var excerpt = TimelineTrim.Extract(source, 1, 3);
        var full = KrlExporter.Export(source, settings);
        var section = KrlExporter.Export(excerpt, settings);

        var point = KrlExporter.WorldToBase(
            new Vector3(30, 4, 8), settings.RobrootWorldPos, settings.BaseDataOffset, settings.SliceBedWorldZ);
        Assert.Equal(LinAt(full, point), LinAt(section, point));
        Assert.Contains("E1 90.000", LinAt(section, point), StringComparison.Ordinal);
    }

    private static KrlExportSettings PrintSettings() => new()
    {
        ProgramName = "section",
        PrintSpeedMps = 0.100f,
        ExtrusionRpmPercent = 40f,
        FirstLayerSpeedMps = 0.040f,
        FirstLayerRpmPercent = 22f,
        DigitalStartStopEnabled = true,
        HeaderTemplate = KrlExporter.DefaultUrmHeaderTemplate,
        FooterTemplate = KrlExporter.DefaultFooterTemplate,
        ToolheadOffsetC = 15f,
        RobrootWorldPos = new Vector3(100, 200, 0),
        BaseDataOffset = new Vector3(5, 6, 7),
    };

    private static string LinAt(string krl, Vector3 p)
    {
        string needle =
            $"X {p.X.ToString("F2", CultureInfo.InvariantCulture)}, " +
            $"Y {p.Y.ToString("F2", CultureInfo.InvariantCulture)}, " +
            $"Z {p.Z.ToString("F2", CultureInfo.InvariantCulture)}";
        foreach (var line in krl.Split('\n'))
            if (line.Contains(needle, StringComparison.Ordinal) && line.Contains("C_VEL", StringComparison.Ordinal))
                return line.Trim();
        throw new Xunit.Sdk.XunitException("missing LIN " + needle);
    }

    private static Toolpath Path(params ToolpathLayer[] layers)
    {
        var tp = new Toolpath();
        foreach (var layer in layers)
            tp.Layers.Add(layer);
        return tp;
    }

    private static ToolpathLayer Layer(int index, float z, Vector3 normal, params ToolpathMove[] moves)
    {
        var layer = new ToolpathLayer(index, z) { Height = 3f, PlaneNormal = normal };
        foreach (var move in moves)
            layer.Moves.Add(move);
        return layer;
    }

    private static ToolpathMove Move(Vector3 from, Vector3 to, float e1) => new(from, to, MoveKind.Extrude)
    {
        Normal = Vector3.UnitZ,
        E1Mm = e1,
        TcpYawDeg = 3f,
        KeyframeToolheadA = 7f,
        KeyframeToolheadB = 8f,
        KeyframeToolheadC = 9f,
        PrintSpeedScale = 1f,
    };

    private static ToolpathMove Mill(Vector3 from, Vector3 to, float e1) => new(from, to, MoveKind.Mill)
    {
        Normal = Vector3.UnitZ,
        E1Mm = e1,
    };
}
