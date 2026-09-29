using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Locks in the tilt/spin distinction that decides whether a drag-release needs a real
/// re-slice — confirmed against the actual PlanarSlicer behavior (re-derives Z bounds from the
/// mesh's current world-transformed vertices every time, so translation and Z-spin never change
/// what it produces; only a tilt does).
/// </summary>
public sealed class DragClassifierTest
{
    [Fact]
    public void Plain_translation_is_not_a_tilt()
    {
        var before = Matrix4.Identity;
        var after  = Matrix4.CreateTranslation(120f, -40f, 15f);

        Assert.False(DragClassifier.ChangedUpAxis(before, after));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(37f)]
    [InlineData(90f)]
    [InlineData(180f)]
    [InlineData(-45f)]
    public void Spin_around_up_axis_is_not_a_tilt(float degrees)
    {
        var before = Matrix4.CreateTranslation(500f, -200f, 70f);
        var after  = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(degrees)) * before;

        Assert.False(DragClassifier.ChangedUpAxis(before, after));
    }

    [Theory]
    [InlineData(90f)]
    [InlineData(-90f)]
    [InlineData(180f)]
    [InlineData(30f)]
    public void Rotation_around_x_is_a_tilt(float degrees)
    {
        var before = Matrix4.Identity;
        var after  = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(degrees));

        Assert.True(DragClassifier.ChangedUpAxis(before, after));
    }

    [Theory]
    [InlineData(90f)]
    [InlineData(-90f)]
    [InlineData(45f)]
    public void Rotation_around_y_is_a_tilt(float degrees)
    {
        var before = Matrix4.Identity;
        var after  = Matrix4.CreateRotationY(MathHelper.DegreesToRadians(degrees));

        Assert.True(DragClassifier.ChangedUpAxis(before, after));
    }

    [Fact]
    public void Combined_translate_and_z_spin_is_still_not_a_tilt()
    {
        var before = Matrix4.CreateTranslation(10f, 20f, 30f);
        var after  = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(60f))
                     * Matrix4.CreateTranslation(400f, -100f, 30f);

        Assert.False(DragClassifier.ChangedUpAxis(before, after));
    }

    [Fact]
    public void A_tiny_numerical_wobble_around_up_axis_is_not_a_tilt()
    {
        // Guards the epsilon isn't so tight that ordinary floating-point noise from a real
        // gizmo drag session gets misclassified as a tilt.
        var before = Matrix4.Identity;
        var after  = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(0.01f));

        Assert.False(DragClassifier.ChangedUpAxis(before, after));
    }

    [Fact]
    public void World_pose_unchanged_by_rotary_reparent_is_not_a_tilt()
    {
        // LFAM 3: user CAD hangs off the rotary pivot (baseAbc C ≈ -90°). Cell-swap /
        // BASE #6 reparent rewrites LocalTransform so World = Local * Parent stays put.
        // Comparing LOCALs looks like a 90° tilt and used to auto-slice on a plain move.
        var rotary = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(-90f))
                     * Matrix4.CreateTranslation(2134.44f, -52.88f, 893.67f);
        var localOnRotary = Matrix4.CreateTranslation(120f, -40f, 15f);
        var worldBefore = localOnRotary * rotary;
        var localOnHeated = worldBefore; // identity parent after BASE #6 rehome
        var worldAfter = localOnHeated;

        Assert.True(DragClassifier.ChangedUpAxis(localOnRotary, localOnHeated),
            "locals diverge — callers must not pass these");
        Assert.False(DragClassifier.ChangedUpAxis(worldBefore, worldAfter));
    }

    [Fact]
    public void World_translation_under_rotary_parent_is_not_a_tilt()
    {
        var parent = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(-90f))
                     * Matrix4.CreateTranslation(2134.44f, -52.88f, 893.67f);
        var local0 = Matrix4.CreateTranslation(10f, 20f, 30f);
        var world0 = local0 * parent;
        var world1 = Matrix4.CreateTranslation(400f, -200f, 0f) * world0;

        Assert.False(DragClassifier.ChangedUpAxis(world0, world1));
    }

    [Fact]
    public void Paused_realtime_never_reslices_on_release_even_if_tilted()
    {
        var before = Matrix4.Identity;
        var after  = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(90f));
        Assert.True(DragClassifier.ChangedUpAxis(before, after));
        Assert.False(DragClassifier.ShouldResliceOnRelease(
            realtimePaused: true, before, after, scaled: false));
        Assert.False(DragClassifier.ShouldResliceOnRelease(
            realtimePaused: true, before, after, scaled: true));
    }

    [Fact]
    public void Unpaused_move_does_not_reslice()
    {
        var before = Matrix4.CreateTranslation(10f, 20f, 30f);
        var after  = Matrix4.CreateTranslation(400f, -100f, 30f);
        Assert.False(DragClassifier.ShouldResliceOnRelease(
            realtimePaused: false, before, after, scaled: false));
    }
}
