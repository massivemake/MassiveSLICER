using System.Numerics;
using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.Tests;

public sealed class KukaOrientationMillTest
{
    [Fact]
    public void Mill_flat_top_points_cutter_down()
    {
        var (a, b, c) = KukaOrientation.AbcFromMillNormal(Vector3.UnitZ);
        var m = KukaIkSolver.AbcToMatrix(a, b, c);
        var toolZ = new Vector3(m.M31, m.M32, m.M33);
        Assert.True(toolZ.Z < -0.99f, $"expected tool Z down, got {toolZ} ABC=({a:0.#},{b:0.#},{c:0.#})");
    }

    [Fact]
    public void Mill_vertical_wall_points_cutter_at_wall()
    {
        var (a, b, c) = KukaOrientation.AbcFromMillNormal(Vector3.UnitX);
        var m = KukaIkSolver.AbcToMatrix(a, b, c);
        var toolZ = new Vector3(m.M31, m.M32, m.M33);
        Assert.True(toolZ.X < -0.99f, $"expected tool Z = -X (into +X wall), got {toolZ} ABC=({a:0.#},{b:0.#},{c:0.#})");
    }

    [Fact]
    public void Mill_y_tilt_changes_abc_from_flat()
    {
        var flat = KukaOrientation.AbcFromMillNormal(Vector3.UnitZ);
        var tilted = KukaOrientation.AbcFromMillNormal(Vector3.UnitZ, 0f, 0f, -15f, 0f);
        Assert.False(MathF.Abs(flat.B - tilted.B) < 0.5f && MathF.Abs(flat.C - tilted.C) < 0.5f,
            $"Y=-15 should change ABC, flat=({flat.A:0.#},{flat.B:0.#},{flat.C:0.#}) tilted=({tilted.A:0.#},{tilted.B:0.#},{tilted.C:0.#})");
    }

    [Fact]
    public void Upright_cutter_keeps_tool_Y_on_world_X()
    {
        var (x, y, z) = KukaOrientation.MillToolAxes(Vector3.UnitZ);
        Assert.True(Vector3.Dot(z, -Vector3.UnitZ) > 0.999f, $"Z {z}");
        Assert.True(Vector3.Dot(x, Vector3.UnitY) > 0.999f, $"X {x}");
        Assert.True(Vector3.Dot(y, Vector3.UnitX) > 0.999f, $"Y {y}");
    }

    [Fact]
    public void Leaning_the_cutter_does_not_flip_tool_Y()
    {
        var upright = KukaOrientation.MillToolAxes(Vector3.UnitZ);
        var near = AxesAtLean(24f, towardY: true);
        var past = AxesAtLean(28f, towardY: true);
        float jump = AngleDeg(near.Y, past.Y);
        Assert.True(jump < 15f, $"a 4° lean swung Y by {jump:0.#}° ({near.Y} -> {past.Y})");

        var plusX = AxesAtLean(44f, towardY: false);
        var minusX = AxesAtLean(-44f, towardY: false);
        Assert.True(Vector3.Dot(upright.Y, plusX.Y) > 0.5f, $"lean +X flipped Y {plusX.Y}");
        Assert.True(Vector3.Dot(plusX.Y, minusX.Y) > 0.0f, $"opposite leans flipped Y {plusX.Y} vs {minusX.Y}");
    }

    static (Vector3 X, Vector3 Y, Vector3 Z) AxesAtLean(float tiltDeg, bool towardY)
    {
        float t = tiltDeg * MathF.PI / 180f;
        var cutter = towardY
            ? new Vector3(0f, MathF.Sin(t), -MathF.Cos(t))
            : new Vector3(MathF.Sin(t), 0f, -MathF.Cos(t));
        return KukaOrientation.MillToolAxes(-cutter);
    }

    static float AngleDeg(Vector3 a, Vector3 b)
        => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f))
           * (180f / MathF.PI);

    [Fact]
    public void Print_flat_top_still_uses_tool_X_approach()
    {
        var (a, b, c) = KukaOrientation.AbcFromNormal(Vector3.UnitZ);
        var m = KukaIkSolver.AbcToMatrix(a, b, c);
        var toolX = new Vector3(m.M11, m.M12, m.M13);
        Assert.True(toolX.Z < -0.99f, $"print approach is tool X down, got {toolX}");
    }
}
