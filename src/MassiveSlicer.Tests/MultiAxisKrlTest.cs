using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>Validates multi-axis KRL export: spindle A/B/C orientation follows the surface normal.</summary>
public class MultiAxisKrlTest(ITestOutputHelper output)
{
    private static MillSettings Mill() => new()
    {
        ToolDiameterMm = 4f, ToolEnd = ToolEndType.Ball, StepoverMm = 4f, RapidZMm = 10f,
    };

    private static KrlExportSettings Export() => new()
    {
        ProgramName = "MA", IsMilling = true, ToolDataIndex = 3,
        CuttingFeedMmMin = 2000f, PlungeFeedMmMin = 800f, ApproachZMm = 20f,
    };

    private static List<(float A, float B, float C)> ParseAbc(string krl)
    {
        var rx = new Regex(@"A (-?\d+\.\d+), B (-?\d+\.\d+), C (-?\d+\.\d+)");
        var list = new List<(float, float, float)>();
        foreach (Match m in rx.Matches(krl))
            list.Add((float.Parse(m.Groups[1].Value), float.Parse(m.Groups[2].Value), float.Parse(m.Groups[3].Value)));
        return list;
    }

    private static float AbcDiff((float A, float B, float C) x, (float A, float B, float C) y)
        => MathF.Abs(x.A - y.A) + MathF.Abs(x.B - y.B) + MathF.Abs(x.C - y.C);

    [Fact]
    public void TiltedSurface_ReorientsSpindle_VsFlat()
    {
        int[] idx = [0, 1, 2, 0, 2, 3];

        // Flat plane z=0, normals +Z -> spindle vertical everywhere.
        Vector3[] flatPos = [new(0, 0, 0), new(20, 0, 0), new(20, 20, 0), new(0, 20, 0)];
        Vector3[] flatNrm = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        var flat = SurfaceFollowMillGenerator.Generate(flatPos, flatNrm, idx, Mill());

        // Plane z=0.3x, constant tilted normal.
        var n = Vector3.Normalize(new Vector3(-0.3f, 0f, 1f));
        Vector3[] tiltPos = [new(0, 0, 0), new(20, 0, 6), new(20, 20, 6), new(0, 20, 0)];
        Vector3[] tiltNrm = [n, n, n, n];
        var tilt = SurfaceFollowMillGenerator.Generate(tiltPos, tiltNrm, idx, Mill());

        var es = Export();
        var flatAbc = ParseAbc(KrlExporter.Export(flat, es));
        var tiltAbc = ParseAbc(KrlExporter.Export(tilt, es));
        Assert.NotEmpty(flatAbc);
        Assert.NotEmpty(tiltAbc);

        var vertical = flatAbc[0];
        output.WriteLine($"vertical ABC = {vertical}");
        // Flat surface: every move stays at the vertical orientation.
        Assert.All(flatAbc, t => Assert.True(AbcDiff(t, vertical) < 1f, $"flat move not vertical: {t}"));
        // Tilted surface: cut moves must reorient the spindle (tool axis follows the normal).
        Assert.Contains(tiltAbc, t => AbcDiff(t, vertical) > 5f);

        var tiltedCut = tiltAbc.First(t => AbcDiff(t, vertical) > 5f);
        output.WriteLine($"tilted cut ABC = {tiltedCut} (atan(0.3) ~= 16.7 deg)");
    }
}
