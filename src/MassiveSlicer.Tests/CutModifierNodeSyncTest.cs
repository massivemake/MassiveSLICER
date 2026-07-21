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
    public void Horizontal_position_and_offset_round_trip_through_the_world_transform()
    {
        var bedCenter = new Vector3(1475.5f, -609.3f, 70f);
        var transform = CutModifierNodeSync.BuildHorizontalTransform(
            positionX: 42f, positionY: -17f, offset: 123.5f, bedCenter);

        var (px, py, offset) = CutModifierNodeSync.ExtractHorizontal(transform, bedCenter);
        Assert.Equal(42f, px, 3);
        Assert.Equal(-17f, py, 3);
        Assert.Equal(123.5f, offset, 3);
    }

    [Fact]
    public void Horizontal_transform_offsets_from_bed_center_with_no_rotation()
    {
        var bedCenter = new Vector3(500f, -300f, 70f);
        var transform = CutModifierNodeSync.BuildHorizontalTransform(
            positionX: 10f, positionY: 20f, offset: 50f, bedCenter);

        Assert.Equal(bedCenter.X + 10f, transform.Row3.X, 3);
        Assert.Equal(bedCenter.Y + 20f, transform.Row3.Y, 3);
        Assert.Equal(bedCenter.Z + 50f, transform.Row3.Z, 3);
        Assert.Equal(Vector3.UnitX, transform.Row0.Xyz);
        Assert.Equal(Vector3.UnitZ, transform.Row2.Xyz);
    }

    [Theory]
    [InlineData(0f, 100f, 0f, 0f)]
    [InlineData(90f, 50f, 0f, 0f)]
    [InlineData(37f, 10f, -80f, 60f)]
    [InlineData(-45f, 200f, 300f, -120f)]
    [InlineData(180f, 75f, -5f, 45f)]
    public void Vertical_offset_rotation_positionZ_and_positionTangent_round_trip_through_the_transform(
        float rotationDegrees, float offset, float positionZ, float positionTangent)
    {
        var bedCenter = new Vector3(1475.5f, -609.3f, 70f);

        var transform = CutModifierNodeSync.BuildVerticalTransform(
            rotationDegrees, offset, positionZ, positionTangent, bedCenter);
        var (extractedOffset, extractedRotation, extractedPositionZ, extractedPositionTangent) =
            CutModifierNodeSync.ExtractVertical(transform, bedCenter);

        Assert.Equal(offset, extractedOffset, 2);
        Assert.Equal(positionZ, extractedPositionZ, 2);
        Assert.Equal(positionTangent, extractedPositionTangent, 2);
        // Normalize both angles into [0,360) before comparing, since -45 and 315 are the same direction.
        float Norm(float deg) => ((deg % 360f) + 360f) % 360f;
        Assert.Equal(Norm(rotationDegrees), Norm(extractedRotation), 2);
    }

    [Fact]
    public void Vertical_at_zero_degrees_faces_positive_x_matching_CutModifierGeometry()
    {
        var bedCenter = Vector3.Zero;
        var transform = CutModifierNodeSync.BuildVerticalTransform(
            rotationDegrees: 0f, offset: 25f, positionZ: 0f, positionTangent: 0f, bedCenter);

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
        var transform = CutModifierNodeSync.BuildVerticalTransform(
            rotationDegrees: 90f, offset: 10f, positionZ: 0f, positionTangent: 0f, bedCenter);

        // At 90 degrees the normal is +Y, so translation should be bedCenter + (0,10,0).
        Assert.Equal(new Vector3(500f, -290f, 70f), transform.Row3.Xyz);
    }

    [Fact]
    public void Vertical_positionZ_moves_the_plane_up_and_down_independent_of_offset_and_rotation()
    {
        var bedCenter = new Vector3(500f, -300f, 70f);
        var transform = CutModifierNodeSync.BuildVerticalTransform(
            rotationDegrees: 37f, offset: 10f, positionZ: 150f, positionTangent: 0f, bedCenter);

        Assert.Equal(bedCenter.Z + 150f, transform.Row3.Z, 3);
    }

    [Fact]
    public void Vertical_positionTangent_moves_the_plane_sideways_along_its_own_facing_direction()
    {
        // At 0 degrees the normal is +X, so the tangent (perpendicular, in-plane) is +Y —
        // this is exactly the "drag the green/Y arrow at rotation 0" case Jeff reported as
        // completely locked before PositionTangent existed.
        var bedCenter = new Vector3(500f, -300f, 70f);
        var transform = CutModifierNodeSync.BuildVerticalTransform(
            rotationDegrees: 0f, offset: 10f, positionZ: 0f, positionTangent: 40f, bedCenter);

        Assert.Equal(new Vector3(510f, -260f, 70f), transform.Row3.Xyz);
    }

    [Fact]
    public void Vertical_dragging_purely_along_world_X_at_45_degrees_does_not_also_move_Y()
    {
        // Reproduces Jeff's exact report: at rotation 45 (normal and tangent both diagonal),
        // a world-space drag purely along X used to get reprojected entirely onto the single
        // normal axis, dragging Y along with it. With PositionTangent tracked separately, a
        // pure-X world delta must decompose into an Offset change AND a PositionTangent change
        // that, added back together via normal/tangent, reconstruct exactly that pure-X delta —
        // not a diagonal one.
        var bedCenter = Vector3.Zero;
        var before = CutModifierNodeSync.BuildVerticalTransform(
            rotationDegrees: 45f, offset: 0f, positionZ: 0f, positionTangent: 0f, bedCenter);

        // Simulate ProcessTranslateDrag: only Row3 changes, by a pure world-X delta.
        var dragged = before;
        dragged.Row3 = before.Row3 + new Vector4(20f, 0f, 0f, 0f);

        var (offset, rotation, positionZ, positionTangent) = CutModifierNodeSync.ExtractVertical(dragged, bedCenter);
        var rebuilt = CutModifierNodeSync.BuildVerticalTransform(rotation, offset, positionZ, positionTangent, bedCenter);

        Assert.Equal(dragged.Row3.X, rebuilt.Row3.X, 2);
        Assert.Equal(dragged.Row3.Y, rebuilt.Row3.Y, 2);
        Assert.Equal(dragged.Row3.Z, rebuilt.Row3.Z, 2);
    }
}
