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
    public void Print_flat_top_still_uses_tool_X_approach()
    {
        var (a, b, c) = KukaOrientation.AbcFromNormal(Vector3.UnitZ);
        var m = KukaIkSolver.AbcToMatrix(a, b, c);
        var toolX = new Vector3(m.M11, m.M12, m.M13);
        Assert.True(toolX.Z < -0.99f, $"print approach is tool X down, got {toolX}");
    }
}
