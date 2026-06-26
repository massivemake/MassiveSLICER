using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public class PickerTest
{
    [Fact]
    public void FindSelectableRoot_Prefers_Deepest_Selectable_Over_RotaryBed_Parent()
    {
        var scene = new SceneNode { Name = "Scene" };
        var rotary = new SceneNode { Name = "RotaryBed", Selectable = true, PickTier = PickTier.Environment };
        var pivot = new SceneNode { Name = "Pivot", Selectable = false, PickTier = PickTier.Environment };
        var scan = new SceneNode { Name = "Scan 12-00-00", Selectable = true, PickTier = PickTier.Content };
        scene.AddChild(rotary);
        rotary.AddChild(pivot);
        pivot.AddChild(scan);

        Assert.Same(scan, Picker.FindSelectableRoot(scan));
        Assert.NotSame(rotary, Picker.FindSelectableRoot(scan));
    }

    [Fact]
    public void FindSelectableRoot_Returns_Null_When_Subtree_Not_Selectable()
    {
        var scene = new SceneNode { Name = "Scene", Selectable = false };
        var rotary = new SceneNode { Name = "RotaryBed", Selectable = false };
        var mesh = new SceneNode { Name = "rotary top", Selectable = false };
        scene.AddChild(rotary);
        rotary.AddChild(mesh);

        Assert.Null(Picker.FindSelectableRoot(mesh));
    }
}