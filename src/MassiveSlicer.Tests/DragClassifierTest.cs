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
}
