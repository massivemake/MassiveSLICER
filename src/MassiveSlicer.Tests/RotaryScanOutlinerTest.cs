using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public class RotaryScanOutlinerTest
{
    [Fact]
    public void AddScanNode_Nests_Under_Selected_Import_In_Outliner()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        var import = new SceneNode { Name = "part.glb", Selectable = true };
        var scan = new SceneNode { Name = "Scan 12-00-00", Selectable = true, PickTier = PickTier.Content };

        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddImportNode(import);
        vm.GetSelectedSceneNode = () => import;

        vm.AddScanNode(scan);

        var rotary = vm.OutlinerItems.First(i => i.Name == "Rotary Bed");
        var importItem = rotary.Children.First(c => c.Node == import);
        Assert.Single(importItem.Children);
        Assert.Same(scan, importItem.Children[0].Node);
    }
}