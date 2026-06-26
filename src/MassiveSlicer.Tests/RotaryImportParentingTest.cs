using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class RotaryImportParentingTest
{
    [Fact]
    public void ReparentUnderPivot_Preserves_WorldPose_At_Current_E1()
    {
        var pivotParent = new SceneNode
        {
            Name = "RotaryBed",
            LocalTransform = Matrix4.CreateTranslation(1000, 500, 200),
        };
        var pivot = new SceneNode
        {
            Name = "RotaryBed_Top",
            LocalTransform = Matrix4.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f),
        };
        pivotParent.AddChild(pivot);

        var worldPose = Matrix4.CreateTranslation(1200, 600, 250);
        var import = new SceneNode { Name = "part", LocalTransform = worldPose };

        import.LocalTransform = import.LocalTransform * pivot.WorldTransform.Inverted();
        pivot.AddChild(import);

        Assert.Equal(worldPose.M41, import.WorldTransform.M41, 3);
        Assert.Equal(worldPose.M42, import.WorldTransform.M42, 3);
        Assert.Equal(worldPose.M43, import.WorldTransform.M43, 3);
    }

    [Fact]
    public void Child_Under_Pivot_Rotates_With_E1()
    {
        var pivot = new SceneNode { Name = "Pivot" };
        var import = new SceneNode
        {
            Name = "part",
            LocalTransform = Matrix4.CreateTranslation(200, 0, 0),
        };
        pivot.AddChild(import);

        float beforeX = import.WorldTransform.M41;
        pivot.LocalTransform = Matrix4.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);

        Assert.NotEqual(beforeX, import.WorldTransform.M41, 1);
        Assert.Equal(0, import.WorldTransform.M41, 1);
        Assert.Equal(200, import.WorldTransform.M42, 1);
    }
}