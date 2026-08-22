using System.Linq;
using System.Numerics;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class MillPlanarOrientationTest
{
    [Fact]
    public void Default_world_negZ_approaches_from_plusZ()
    {
        var tool = MillPlanarOrientation.ResolveToolAxis(
            MillPlanarAxisKind.WorldNegZ, Vector3.Zero, 0, 0);
        Assert.True(Vector3.Dot(tool, -Vector3.UnitZ) > 0.999f);
        var approach = MillPlanarOrientation.ApproachFromToolAxis(tool);
        Assert.True(Vector3.Dot(approach, Vector3.UnitZ) > 0.999f);
    }

    [Fact]
    public void Tilt_30deg_off_down_is_not_vertical()
    {
        var tool = MillPlanarOrientation.ResolveToolAxis(
            MillPlanarAxisKind.WorldNegZ, Vector3.Zero, 30f, 0f);
        Assert.InRange(Vector3.Dot(tool, -Vector3.UnitZ), 0.85f, 0.88f);
        Assert.True(MathF.Abs(tool.X) > 0.4f || MathF.Abs(tool.Y) > 0.4f);
        Assert.InRange(tool.Length(), 0.999f, 1.001f);
    }

    [Fact]
    public void World_posX_tool_ABC_points_cutter_plusX()
    {
        var tool = MillPlanarOrientation.ResolveToolAxis(
            MillPlanarAxisKind.WorldPosX, Vector3.Zero, 0, 0);
        var n = MillPlanarOrientation.SurfaceNormalFromToolAxis(tool);
        var (a, b, c) = KukaOrientation.AbcFromMillNormal(n);
        var m = KukaIkSolver.AbcToMatrix(a, b, c);
        var toolZ = new Vector3(m.M31, m.M32, m.M33);
        Assert.True(toolZ.X > 0.99f, $"expected T12 +Z = +X, got {toolZ}");
    }

    [Fact]
    public void Planar_generate_along_plusX_covers_vertical_wall()
    {
        Vector3[] pos = [new(0, 0, 0), new(0, 20, 0), new(0, 20, 20), new(0, 0, 20)];
        Vector3[] nrm = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX];
        int[] idx = [0, 1, 2, 0, 2, 3];
        var mill = new MillSettings
        {
            ToolDiameterMm = 4f, ToolEnd = ToolEndType.Flat, StepoverMm = 4f, RapidZMm = 10f,
        };

        var down = SurfaceFollowMillGenerator.Generate(pos, nrm, idx, mill);
        var side = SurfaceFollowMillGenerator.Generate(
            pos, nrm, idx, mill, approachAxis: Vector3.UnitX, lockToolToApproach: true);

        int downCuts = down.Layers.Sum(l => l.Moves.Count(m => m.Kind == MoveKind.Mill));
        var sideCuts = side.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Mill).ToList();
        Assert.True(sideCuts.Count > downCuts + 5, $"side={sideCuts.Count} down={downCuts}");
        Assert.All(sideCuts, m =>
        {
            Assert.InRange(m.To.X, -0.05f, 0.05f);
            Assert.True(Vector3.Dot(Vector3.Normalize(m.Normal), Vector3.UnitX) > 0.999f);
        });

        var (a, b, c) = KukaOrientation.AbcFromMillNormal(sideCuts[0].Normal);
        var mat = KukaIkSolver.AbcToMatrix(a, b, c);
        var toolZ = new Vector3(mat.M31, mat.M32, mat.M33);
        Assert.True(toolZ.X < -0.99f, $"T12 +Z should point into the +X wall, got {toolZ}");
    }

    [Fact]
    public void Positive_offset_pushes_path_out_along_surface_normal()
    {
        Vector3[] pos = [new(0, 0, 0), new(0, 20, 0), new(0, 20, 20), new(0, 0, 20)];
        Vector3[] nrm = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX];
        int[] idx = [0, 1, 2, 0, 2, 3];
        var mill = new MillSettings
        {
            ToolDiameterMm = 4f, ToolEnd = ToolEndType.Flat, StepoverMm = 4f, RapidZMm = 10f,
            OffsetDistanceMm = 3f,
        };

        var side = SurfaceFollowMillGenerator.Generate(
            pos, nrm, idx, mill, approachAxis: Vector3.UnitX, lockToolToApproach: true);
        var cuts = side.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Mill).ToList();
        Assert.NotEmpty(cuts);
        Assert.All(cuts, m => Assert.InRange(m.To.X, 2.95f, 3.05f));
    }

    [Fact]
    public void Average_normal_of_plusX_quad()
    {
        Vector3[] pos = [new(0, 0, 0), new(0, 10, 0), new(0, 10, 10), new(0, 0, 10)];
        Vector3[] nrm = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX];
        int[] idx = [0, 1, 2, 0, 2, 3];
        var n = MillPlanarOrientation.AverageSurfaceNormal(pos, nrm, idx);
        Assert.True(Vector3.Dot(n, Vector3.UnitX) > 0.99f);
    }
}
