using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;
using MassiveSlicer.Viewport.Scene.Modifiers;
using OpenTK.Mathematics;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class CutModifierGeometryTest
{
    private static MeshData UnitCube()
    {
        float h = 0.5f;
        var p = new Vector3[]
        {
            new(-h,-h,-h), new( h,-h,-h), new( h, h,-h), new(-h, h,-h),
            new(-h,-h, h), new( h,-h, h), new( h, h, h), new(-h, h, h),
        };
        uint[] idx =
        [
            0,1,2, 0,2,3,
            4,6,5, 4,7,6,
            0,4,5, 0,5,1,
            2,6,7, 2,7,3,
            0,3,7, 0,7,4,
            1,5,6, 1,6,2,
        ];
        var nrm = new Vector3[p.Length];
        for (int i = 0; i < p.Length; i++)
            nrm[i] = Vector3.Normalize(p[i]);
        return new MeshData(p, nrm, idx, "cube");
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float eps = 1e-4f)
    {
        Assert.True((expected - actual).Length < eps, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void Horizontal_orientation_uses_z_normal_at_offset()
    {
        var modifier = new CutModifier { Orientation = CutOrientation.Horizontal, Offset = 0.25f };

        Assert.Equal(Vector3.UnitZ, CutModifierGeometry.Normal(modifier));
        Assert.Equal(new Vector3(0, 0, 0.25f), CutModifierGeometry.PlanePoint(modifier, Vector3.Zero));
    }

    [Fact]
    public void Horizontal_orientation_ignores_bed_center()
    {
        var modifier = new CutModifier { Orientation = CutOrientation.Horizontal, Offset = 0.25f };

        // Bed center only matters for Vertical — Horizontal stays purely local-Z regardless.
        Assert.Equal(new Vector3(0, 0, 0.25f), CutModifierGeometry.PlanePoint(modifier, new Vector3(100, 200, 0)));
    }

    [Fact]
    public void Vertical_at_zero_degrees_faces_positive_x()
    {
        var modifier = new CutModifier { Orientation = CutOrientation.Vertical, RotationDegrees = 0f, Offset = 0.25f };

        AssertClose(Vector3.UnitX, CutModifierGeometry.Normal(modifier));
        AssertClose(new Vector3(0.25f, 0, 0), CutModifierGeometry.PlanePoint(modifier, Vector3.Zero));
    }

    [Fact]
    public void Vertical_at_ninety_degrees_faces_positive_y()
    {
        var modifier = new CutModifier { Orientation = CutOrientation.Vertical, RotationDegrees = 90f, Offset = 0.25f };

        AssertClose(Vector3.UnitY, CutModifierGeometry.Normal(modifier));
        AssertClose(new Vector3(0, 0.25f, 0), CutModifierGeometry.PlanePoint(modifier, Vector3.Zero));
    }

    [Fact]
    public void Vertical_at_an_arbitrary_manual_angle_is_not_axis_aligned()
    {
        var modifier = new CutModifier { Orientation = CutOrientation.Vertical, RotationDegrees = 37f, Offset = 1f };

        var normal = CutModifierGeometry.Normal(modifier);
        Assert.True(MathF.Abs(normal.X) > 1e-3f && MathF.Abs(normal.Y) > 1e-3f, "expected a non-axis-aligned normal");
        Assert.True(MathF.Abs(normal.Length - 1f) < 1e-4f, "normal should stay unit length");
    }

    [Fact]
    public void Vertical_plane_point_pivots_around_bed_center_not_local_origin()
    {
        var modifier = new CutModifier { Orientation = CutOrientation.Vertical, RotationDegrees = 0f, Offset = 10f };
        var bedCenter = new Vector3(500, -300, 70);

        AssertClose(new Vector3(510, -300, 70), CutModifierGeometry.PlanePoint(modifier, bedCenter));
    }

    [Fact]
    public void Split_horizontal_at_center_yields_two_non_empty_halves()
    {
        var modifier = new CutModifier { Orientation = CutOrientation.Horizontal, Offset = 0f };
        var result = CutModifierGeometry.Split(modifier, UnitCube(), Vector3.Zero);

        Assert.True(result.Positive.Positions.Length >= 3);
        Assert.True(result.Negative.Positions.Length >= 3);
    }

    [Fact]
    public void Split_with_offset_beyond_mesh_yields_one_empty_half()
    {
        var modifier = new CutModifier { Orientation = CutOrientation.Horizontal, Offset = 10f };
        var result = CutModifierGeometry.Split(modifier, UnitCube(), Vector3.Zero);

        Assert.True(result.Positive.Positions.Length == 0);
        Assert.True(result.Negative.Positions.Length >= 3);
    }
}
