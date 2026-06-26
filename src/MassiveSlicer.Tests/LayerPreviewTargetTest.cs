using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public class LayerPreviewTargetTest
{
    [Fact]
    public void SyncLayerPreviewFlags_Applies_Only_To_Active_Print_Object()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", PickTier = PickTier.Environment };
        var robot = new SceneNode { Name = "RobotRoot", Selectable = false, PickTier = PickTier.Environment };
        var import = new SceneNode { Name = "part.glb", Selectable = true };
        var arm = new SceneNode { Name = "Robot Arm", Selectable = false, PickTier = PickTier.Environment };

        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.SetRobotGroup(robot, robot, arm);
        vm.AddImportNode(import);
        vm.GetSelectedSceneNode = () => import;

        vm.SyncLayerPreviewFlags(enabled: true);

        Assert.False(robot.LayerPreview);
        Assert.False(pivot.LayerPreview);
        Assert.True(import.LayerPreview);
    }
}