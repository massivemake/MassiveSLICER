using MassiveSlicer.App;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public sealed class MultiToolOutlinerTest
{
    [Fact]
    public void SetMultiToolOutliner_Adds_Toolhead_Group_With_All_Tools()
    {
        var vm = new ViewportViewModel();
        var extruder = new SceneNode { Name = "Tool_HV Extruder" };
        var scanner  = new SceneNode { Name = "Tool_Scanner" };
        var spindle  = new SceneNode { Name = "Tool_Spindle" };

        vm.SetMultiToolOutliner([
            ("HV Extruder", extruder),
            ("Scanner", scanner),
            ("Spindle", spindle),
        ]);

        var group = vm.OutlinerItems.Single(i => i.Name == "Toolheads");
        Assert.Equal(3, group.Children.Count);
        Assert.Contains(group.Children, c => c.Name == "HV Extruder");
        Assert.Contains(group.Children, c => c.Name == "Scanner");
        Assert.Contains(group.Children, c => c.Name == "Spindle");
        Assert.All(group.Children, c =>
        {
            Assert.False(c.CanDelete);
            Assert.True(c.UsesExclusiveVisibility);
            Assert.False(c.ShowVisibilityToggle);
        });
    }

    [Fact]
    public void SetActiveToolheadOutliner_Highlights_Only_Selected_Row()
    {
        var vm = new ViewportViewModel();
        var extruder = new SceneNode { Name = "Tool_HV Extruder" };
        var scanner  = new SceneNode { Name = "Tool_Scanner" };

        vm.SetMultiToolOutliner([
            ("HV Extruder", extruder),
            ("Scanner", scanner),
        ]);

        var group = vm.OutlinerItems.Single(i => i.Name == "Toolheads");
        var extruderItem = group.Children.Single(c => c.Name == "HV Extruder");
        var scannerItem  = group.Children.Single(c => c.Name == "Scanner");

        vm.SetActiveToolheadOutliner("Scanner");

        Assert.False(extruderItem.IsOutlinerSelected);
        Assert.True(scannerItem.IsOutlinerSelected);
        Assert.True(scannerItem.IsRowHighlighted);
    }

    [Fact]
    public void FindToolheadOutlinerItem_Resolves_Flange_Node()
    {
        var vm = new ViewportViewModel();
        var scanner = new SceneNode { Name = "Tool_Scanner" };
        vm.SetMultiToolOutliner([("Scanner", scanner)]);

        var item = vm.FindToolheadOutlinerItem(scanner);
        Assert.NotNull(item);
        Assert.Equal("Scanner", item!.Name);
    }

    [Fact]
    public void Toolhead_Items_Are_Not_User_Models()
    {
        var vm = new ViewportViewModel();
        vm.SetMultiToolOutliner([("Scanner", new SceneNode { Name = "Tool_Scanner" })]);

        var scanner = vm.OutlinerItems.Single(i => i.Name == "Toolheads").Children.Single();
        Assert.True(OutlinerModelOps.IsToolheadItem(scanner));
        Assert.Null(vm.FindUserMeshOutlinerItem(scanner.Node));
    }
}