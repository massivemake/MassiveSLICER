using System;
using System.Linq;
using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>Validates the multi-axis surface-follow toolpath: contacts on the surface, tool axis = normal.</summary>
public class SurfaceFollowMillTest(ITestOutputHelper output)
{
    private static MillSettings Mill() => new()
    {
        ToolDiameterMm = 4f, ToolEnd = ToolEndType.Ball, StepoverMm = 4f, RapidZMm = 10f,
    };

    [Fact]
    public void TiltedPlane_CutsRideSurface_AndToolAxisFollowsNormal()
    {
        // Plane z = 0.3*x over a 20x20 patch; constant normal = normalize(-0.3, 0, 1).
        var n = Vector3.Normalize(new Vector3(-0.3f, 0f, 1f));
        Vector3[] pos = [new(0, 0, 0), new(20, 0, 6), new(20, 20, 6), new(0, 20, 0)];
        Vector3[] nrm = [n, n, n, n];
        int[] idx = [0, 1, 2, 0, 2, 3];

        var tp = SurfaceFollowMillGenerator.Generate(pos, nrm, idx, Mill());
        Assert.Single(tp.Layers);

        var cuts = tp.Layers[0].Moves.Where(m => m.Kind == MoveKind.Mill).ToList();
        Assert.NotEmpty(cuts);
        output.WriteLine($"{cuts.Count} cut moves, {tp.Layers[0].Moves.Count} total");

        foreach (var m in cuts)
        {
            Assert.InRange(m.To.Z, 0.3f * m.To.X - 0.02f, 0.3f * m.To.X + 0.02f);     // on the plane
            Assert.True(Vector3.Dot(Vector3.Normalize(m.Normal), n) > 0.999f,          // tool axis = surface normal
                $"tool axis {m.Normal} should match plane normal {n}");
        }

        // Cuts must travel above the surface peak between passes (real retracts present).
        Assert.Contains(tp.Layers[0].Moves, m => m.Kind == MoveKind.Travel && m.IsZHop);
    }

    [Fact]
    public void FlatPlane_ToolAxisIsVertical()
    {
        Vector3[] pos = [new(0, 0, 2), new(30, 0, 2), new(30, 30, 2), new(0, 30, 2)];
        Vector3[] nrm = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        int[] idx = [0, 1, 2, 0, 2, 3];

        var tp = SurfaceFollowMillGenerator.Generate(pos, nrm, idx, Mill());
        var cuts = tp.Layers[0].Moves.Where(m => m.Kind == MoveKind.Mill).ToList();

        Assert.NotEmpty(cuts);
        Assert.All(cuts, m =>
        {
            Assert.InRange(m.To.Z, 1.98f, 2.02f);                                   // flat at z=2
            Assert.True(Vector3.Dot(Vector3.Normalize(m.Normal), Vector3.UnitZ) > 0.999f);
        });
    }
}
