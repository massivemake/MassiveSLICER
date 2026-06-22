using System;
using System.Collections.Generic;
using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>Validates the gouge/residual fail-rate analysis of a surface-follow toolpath.</summary>
public class ToolpathSurfaceDeviationTest(ITestOutputHelper output)
{
    private static (Vector3[] pos, Vector3[] nrm, int[] idx) FlatPlane(float size) =>
    (
        [new(0, 0, 0), new(size, 0, 0), new(size, size, 0), new(0, size, 0)],
        [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        [0, 1, 2, 0, 2, 3]
    );

    // Dense grid of ideal surface samples on the flat plane.
    private static List<Vector3> SampleGrid(float size, float step)
    {
        var pts = new List<Vector3>();
        for (float y = 0; y <= size; y += step)
            for (float x = 0; x <= size; x += step)
                pts.Add(new Vector3(x, y, 0));
        return pts;
    }

    private static MillSettings Mill(float stepover) => new()
    {
        ToolDiameterMm = 4f, ToolEnd = ToolEndType.Ball, StepoverMm = stepover, RapidZMm = 10f,
    };

    [Fact]
    public void FlatSurface_NoGouge_AndFinerStepoverLeavesLessResidual()
    {
        var (pos, nrm, idx) = FlatPlane(40f);
        var samples = SampleGrid(40f, 1f);
        const float r = 2f;     // ToolDiameter 4 -> radius 2

        var coarse = ToolpathSurfaceDeviation.Analyze(
            samples, SurfaceFollowMillGenerator.Generate(pos, nrm, idx, Mill(8f)), r, 0.1f);
        var fine = ToolpathSurfaceDeviation.Analyze(
            samples, SurfaceFollowMillGenerator.Generate(pos, nrm, idx, Mill(2f)), r, 0.1f);

        output.WriteLine($"coarse: gouge={coarse.GougePct:F1}% residual={coarse.ResidualPct:F1}% " +
                         $"ok={coarse.OkPct:F1}% maxRes={coarse.MaxResidualMm:F3}");
        output.WriteLine($"fine:   gouge={fine.GougePct:F1}% residual={fine.ResidualPct:F1}% " +
                         $"ok={fine.OkPct:F1}% maxRes={fine.MaxResidualMm:F3}");

        // A flat surface can't be gouged by a ball following its normal.
        Assert.Equal(0f, coarse.GougePct, 3);
        Assert.Equal(0f, fine.GougePct, 3);
        // Percentages are a valid partition.
        Assert.InRange(coarse.GougePct + coarse.ResidualPct + coarse.OkPct, 99.9f, 100.1f);
        // Finer stepover -> smaller scallops -> less residual and a lower peak cusp.
        Assert.True(fine.ResidualPct <= coarse.ResidualPct + 1e-3f);
        Assert.True(fine.MaxResidualMm < coarse.MaxResidualMm,
            $"finer stepover should cut closer: fine {fine.MaxResidualMm} vs coarse {coarse.MaxResidualMm}");
    }
}
