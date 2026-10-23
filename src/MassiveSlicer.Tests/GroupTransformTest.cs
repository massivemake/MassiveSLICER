using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public sealed class GroupTransformTest
{
    [Fact]
    public void Translation_moves_every_member_by_the_same_world_delta()
    {
        var op = GroupTransform.Translation(new Vector3(0f, 5f, 0f));
        var a = GroupTransform.LocalFromWorld(Matrix4.CreateTranslation(0f, 0f, 0f), op, Matrix4.Identity);
        var b = GroupTransform.LocalFromWorld(Matrix4.CreateTranslation(10f, 0f, 0f), op, Matrix4.Identity);

        Assert.Equal(0f, a.Row3.X, 3);
        Assert.Equal(5f, a.Row3.Y, 3);
        Assert.Equal(10f, b.Row3.X, 3);
        Assert.Equal(5f, b.Row3.Y, 3);
    }

    [Fact]
    public void Rotation_about_the_group_center_keeps_the_spacing()
    {
        var pivot = new Vector3(5f, 0f, 0f);
        var op = GroupTransform.AboutPivot(pivot, Matrix4.CreateRotationZ(MathF.PI / 2f));
        var a = GroupTransform.LocalFromWorld(Matrix4.CreateTranslation(0f, 0f, 0f), op, Matrix4.Identity);
        var b = GroupTransform.LocalFromWorld(Matrix4.CreateTranslation(10f, 0f, 0f), op, Matrix4.Identity);

        var mid = (a.Row3.Xyz + b.Row3.Xyz) * 0.5f;
        Assert.Equal(pivot.X, mid.X, 3);
        Assert.Equal(pivot.Y, mid.Y, 3);
        Assert.Equal(10f, (a.Row3.Xyz - b.Row3.Xyz).Length, 3);
    }

    [Fact]
    public void Scale_about_the_group_center_spreads_the_members()
    {
        var pivot = new Vector3(5f, 0f, 0f);
        var op = GroupTransform.AboutPivot(pivot, Matrix4.CreateScale(2f));
        var a = GroupTransform.LocalFromWorld(Matrix4.CreateTranslation(0f, 0f, 0f), op, Matrix4.Identity);
        var b = GroupTransform.LocalFromWorld(Matrix4.CreateTranslation(10f, 0f, 0f), op, Matrix4.Identity);

        Assert.Equal(-5f, a.Row3.X, 3);
        Assert.Equal(15f, b.Row3.X, 3);
        Assert.Equal(2f, a.Row0.X, 3);
        Assert.Equal(2f, b.Row0.X, 3);
    }

    [Fact]
    public void World_delta_is_expressed_in_the_parent_frame()
    {
        var parent = Matrix4.CreateTranslation(100f, 0f, 0f);
        Matrix4.Invert(parent, out var invParent);
        var initialWorld = Matrix4.Identity * parent;
        var local = GroupTransform.LocalFromWorld(
            initialWorld, GroupTransform.Translation(new Vector3(1f, 0f, 0f)), invParent);

        Assert.Equal(1f, local.Row3.X, 3);
        Assert.Equal(0f, local.Row3.Y, 3);
    }
}
