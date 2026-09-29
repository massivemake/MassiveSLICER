using OpenTK.Mathematics;
using MassiveSlicer.Viewport.Scene;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// SceneRenderer draws toolpaths with LocalTransform × mvp (parent ignored).
/// A rotary parent inverse must never stay in that matrix.
/// </summary>
public sealed class ToolpathScenePoseTest
{
    [Fact]
    public void ShiftDrawLocal_is_translation_only_and_round_trips()
    {
        var local = Matrix4.CreateTranslation(2100f, 10f, 910f);
        var delta = new Vector3(120f, -400f, -780f);

        var moved = ToolpathScenePose.ShiftDrawLocal(local, delta);
        Assert.Equal(2220f, moved.Row3.X, 1);
        Assert.Equal(-390f, moved.Row3.Y, 1);
        Assert.Equal(130f, moved.Row3.Z, 1);
        Assert.Equal(1f, moved.Row0.X, 3);
        Assert.Equal(1f, moved.Row1.Y, 3);
        Assert.Equal(1f, moved.Row2.Z, 3);

        var back = ToolpathScenePose.ShiftDrawLocal(moved, -delta);
        Assert.Equal(2100f, back.Row3.X, 1);
        Assert.Equal(10f, back.Row3.Y, 1);
        Assert.Equal(910f, back.Row3.Z, 1);
    }

    [Fact]
    public void ShiftDrawLocal_strips_rotary_inverse_rotation()
    {
        var centroid = Matrix4.CreateTranslation(2100f, 10f, 910f);
        var rotary = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(-90f))
                     * Matrix4.CreateTranslation(2134.44f, -52.88f, 893.67f);
        var corrupted = centroid * rotary.Inverted();

        Assert.True(MathF.Abs(corrupted.Row0.X - 1f) > 0.1f || MathF.Abs(corrupted.Row1.Y - 1f) > 0.1f,
            "setup: inverse rotary is not identity rotation");

        var parked = ToolpathScenePose.ShiftDrawLocal(corrupted, Vector3.Zero);
        Assert.Equal(1f, parked.Row0.X, 3);
        Assert.Equal(1f, parked.Row1.Y, 3);
        Assert.Equal(1f, parked.Row2.Z, 3);
        Assert.True(parked.Row3.Xyz.LengthSquared > 1f);
    }

    [Fact]
    public void TranslationOnly_keeps_origin_and_drops_tilt()
    {
        var world = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(-90f))
                    * Matrix4.CreateTranslation(500f, 40f, 130f);
        var t = ToolpathScenePose.TranslationOnly(world);
        Assert.Equal(1f, t.Row0.X, 3);
        Assert.Equal(1f, t.Row1.Y, 3);
        var origin = Vector3.TransformPosition(Vector3.Zero, world);
        Assert.Equal(origin.X, t.Row3.X, 1);
        Assert.Equal(origin.Y, t.Row3.Y, 1);
        Assert.Equal(origin.Z, t.Row3.Z, 1);
    }
}
