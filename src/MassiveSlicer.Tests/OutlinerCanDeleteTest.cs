using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class OutlinerCanDeleteTest
{
    [Fact]
    public void Cell_infrastructure_outliner_items_are_not_deletable()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top" };
        var robot = new SceneNode { Name = "RobotRoot" };
        var pedestal = new SceneNode { Name = "Pedestal" };
        var arm = new SceneNode { Name = "Arm" };
        var stand = new SceneNode { Name = "Extruder Stand" };
        var bed = new SceneNode { Name = "Bed_Root" };

        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.SetRobotGroup(robot, pedestal, arm);
        vm.SetCellEnvironmentOutliner([(stand, "Extruder Stand"), (bed, "Print Bed")]);

        var rotary = vm.OutlinerItems.First(i => i.Name == "Rotary Bed");
        var robotItem = vm.OutlinerItems.First(i => i.Name == "Robot Root");
        var standItem = vm.OutlinerItems.First(i => i.Name == "Extruder Stand");
        var bedItem = vm.OutlinerItems.First(i => i.Name == "Print Bed");

        Assert.False(rotary.CanDelete);
        Assert.False(robotItem.CanDelete);
        Assert.False(robotItem.Children.Single(c => c.Name == "Robot Pedestal").CanDelete);
        Assert.False(robotItem.Children.Single(c => c.Name == "Robot Arm").CanDelete);
        Assert.False(standItem.CanDelete);
        Assert.False(bedItem.CanDelete);
    }

    [Fact]
    public void User_import_outliner_items_remain_deletable()
    {
        var vm = new ViewportViewModel();
        var import = new SceneNode { Name = "part.glb", Selectable = true };
        vm.AddImportNode(import);

        var item = vm.OutlinerItems.Single();
        Assert.True(item.CanDelete);
    }
}