using MassiveSlicer.Viewport.Rendering;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public sealed class ContactShadowBuilderTest
{
    [Fact]
    public void BuildProjections_includes_bed_and_objects_on_separate_floor_passes()
    {
        var root = new SceneNode { Name = "Root" };

        var bed = new SceneNode { Name = "Print Bed", Selectable = false };
        bed.PendingMesh = BoxMesh(0f, 0f, 0f, 2000f, 1000f, 20f);
        root.AddChild(bed);

        var pedestal = new SceneNode { Name = "KR_120_R2700-2_BASE" };
        pedestal.PendingMesh = BoxMesh(-200f, -200f, 0f, 200f, 200f, 800f);
        root.AddChild(pedestal);

        var truck = new SceneNode { Name = "Truck A" };
        truck.LocalTransform = Matrix4.CreateTranslation(400f, 300f, 20f);
        truck.PendingMesh = BoxMesh(0f, 0f, 0f, 800f, 400f, 300f);
        root.AddChild(truck);

        var passes = ContactShadowBuilder.BuildProjections(root);

        Assert.Equal(2, passes.Count);
        Assert.True(ContactShadowBuilder.ShouldCastSilhouette(bed, passes[1].FloorZ));
        Assert.True(ContactShadowBuilder.ShouldCastSilhouette(pedestal, passes[0].FloorZ));
        Assert.True(ContactShadowBuilder.ShouldCastSilhouette(truck, passes[1].FloorZ));
        Assert.True(passes[0].FloorZ < passes[1].FloorZ);
    }

    [Fact]
    public void BuildProjections_bed_casts_rim_and_top_passes()
    {
        var root = new SceneNode { Name = "Root" };

        var bed = new SceneNode { Name = "Print Bed", Selectable = false };
        bed.PendingMesh = BoxMesh(0f, 0f, 0f, 2000f, 1000f, 20f);
        root.AddChild(bed);

        var passes = ContactShadowBuilder.BuildProjections(root);

        Assert.Equal(2, passes.Count);
        Assert.Equal(0.6f, passes[0].FloorZ, 0.01f);
        Assert.Equal(20.6f, passes[1].FloorZ, 0.01f);
        Assert.True(passes[1].MaxX - passes[1].MinX > 2000f);
    }

    [Fact]
    public void BuildProjections_elevated_robot_skips_mount_collar_booster_casts_at_ground()
    {
        var root = new SceneNode { Name = "Root" };

        var robot = new SceneNode
        {
            Name           = "LFAM 2_Robot",
            LocalTransform = Matrix4.CreateTranslation(0f, 0f, 1000f),
        };
        var robotBase = new SceneNode { Name = "KR_120_R2700-2_BASE" };
        robotBase.PendingMesh = BoxMesh(-200f, -200f, 0f, 200f, 200f, 180f);
        robot.AddChild(robotBase);
        var arm = new SceneNode { Name = "joint_1" };
        arm.PendingMesh = BoxMesh(0f, 0f, 180f, 400f, 400f, 900f);
        robot.AddChild(arm);
        root.AddChild(robot);

        var booster = new SceneNode { Name = "BoosterFrame" };
        booster.PendingMesh = BoxMesh(-350f, -350f, 0f, 350f, 350f, 1000f);
        root.AddChild(booster);

        var passes = ContactShadowBuilder.BuildProjections(root);

        Assert.Single(passes);
        Assert.Equal(0.6f, passes[0].FloorZ, 0.01f);
        Assert.False(ContactShadowBuilder.ShouldCastShadow(robot));
        Assert.True(ContactShadowBuilder.ShouldCastSilhouette(booster, passes[0].FloorZ));
        Assert.False(ContactShadowBuilder.ShouldCastSilhouette(robot, passes[0].FloorZ));
    }

    [Fact]
    public void BuildProjections_skips_toolpath_nodes()
    {
        var root = new SceneNode { Name = "Root" };

        var mesh = new SceneNode { Name = "Part" };
        mesh.PendingMesh = BoxMesh(0f, 0f, 0f, 100f, 100f, 50f);
        root.AddChild(mesh);

        var toolpath = new SceneNode { Name = "Toolpath (preview)" };
        toolpath.PendingMesh = BoxMesh(0f, 0f, 0f, 10f, 10f, 10f);
        root.AddChild(toolpath);

        var passes = ContactShadowBuilder.BuildProjections(root);

        Assert.Single(passes);
        Assert.False(ContactShadowBuilder.ShouldCastShadow(toolpath));
    }

    private static MeshData BoxMesh(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var positions = new[]
        {
            new Vector3(x0, y0, z0), new Vector3(x1, y0, z0),
            new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
            new Vector3(x0, y0, z1), new Vector3(x1, y0, z1),
            new Vector3(x1, y1, z1), new Vector3(x0, y1, z1),
        };
        uint[] indices =
        [
            0, 1, 2, 0, 2, 3,
            4, 6, 5, 4, 7, 6,
            0, 4, 5, 0, 5, 1,
            1, 5, 6, 1, 6, 2,
            2, 6, 7, 2, 7, 3,
            3, 7, 4, 3, 4, 0,
        ];
        return new MeshData(positions, positions, indices, "box");
    }
}