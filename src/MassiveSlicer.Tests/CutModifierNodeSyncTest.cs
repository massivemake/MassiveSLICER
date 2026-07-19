using MassiveSlicer.Viewport.Scene.Modifiers;
using OpenTK.Mathematics;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Round-trips CutModifier fields through the gizmo node's transform and back — this is the
/// only way to actually verify the rotation-matrix convention (OpenTK row-vector, Row0 = local
/// +X / the plane's normal) is what this code assumes, since the interactive gizmo drag itself
/// can't be exercised outside a real pointer session.
/// </summary>
public sealed class CutModifierNodeSyncTest
{
    [Fact]
    public void Horizontal_offset_round_trips_through_the_local_transform()
    {
        var local = CutModifierNodeSync.BuildHorizontalLocalTransform(offset: 123.5f);

        Assert.Equal(123.5f, CutModifierNodeSync.ExtractHorizontalOffset(local), 3);
    }

    [Fact]
    public void Horizontal_transform_has_no_rotation_or_xy_translation()
    {
        var local = CutModifierNodeSync.BuildHorizontalLocalTransform(offset: 50f);

        Assert.Equal(0f, local.Row3.X, 3);
        Assert.Equal(0f, local.Row3.Y, 3);
        Assert.Equal(Vector3.UnitX, local.Row0.Xyz);
        Assert.Equal(Vector3.UnitZ, local.Row2.Xyz);
    }

    [Theory]
    [InlineData(0f, 100f)]
    [InlineData(90f, 50f)]
    [InlineData(37f, 10f)]
    [InlineData(-45f, 200f)]
    [InlineData(180f, 75f)]
    public void Vertical_offset_and_rotation_round_trip_through_the_transform(float rotationDegrees, float offset)
    {
        var bedCenter = new Vector3(1475.5f, -609.3f, 70f);

        var transform = CutModifierNodeSync.BuildVerticalTransform(rotationDegrees, offset, bedCenter);
        var (extractedOffset, extractedRotation) = CutModifierNodeSync.ExtractVertical(transform, bedCenter);

        Assert.Equal(offset, extractedOffset, 2);
        // Normalize both angles into [0,360) before comparing, since -45 and 315 are the same direction.
        float Norm(float deg) => ((deg % 360f) + 360f) % 360f;
        Assert.Equal(Norm(rotationDegrees), Norm(extractedRotation), 2);
    }

    [Fact]
    public void Vertical_at_zero_degrees_faces_positive_x_matching_CutModifierGeometry()
    {
        var bedCenter = Vector3.Zero;
        var transform = CutModifierNodeSync.BuildVerticalTransform(rotationDegrees: 0f, offset: 25f, bedCenter);

        // Must agree with CutModifierGeometry.Normal/PlanePoint for the same inputs — these two
        // code paths (the live gizmo node, and the plain-data math used by Apply/Split) must
        // never disagree about what a given Offset/RotationDegrees actually means in space.
        Assert.Equal(new Vector3(25f, 0f, 0f), transform.Row3.Xyz);
        Assert.Equal(Vector3.UnitX, transform.Row0.Xyz);
    }

    [Fact]
    public void Vertical_translation_pivots_around_bed_center_not_local_origin()
    {
        var bedCenter = new Vector3(500f, -300f, 70f);
        var transform = CutModifierNodeSync.BuildVerticalTransform(rotationDegrees: 90f, offset: 10f, bedCenter);

        // At 90 degrees the normal is +Y, so translation should be bedCenter + (0,10,0).
        Assert.Equal(new Vector3(500f, -290f, 70f), transform.Row3.Xyz);
    }
}
