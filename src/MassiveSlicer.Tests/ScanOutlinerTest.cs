using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public sealed class ScanOutlinerTest
{
    [Fact]
    public void AddScanNode_Places_Scans_As_Direct_Children_Of_Rotary_Bed()
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
        Assert.Equal(2, rotary.Children.Count);
        Assert.Contains(rotary.Children, c => c.Node == import);
        Assert.Contains(rotary.Children, c => c.Node == scan);
        Assert.DoesNotContain(rotary.Children.First(c => c.Node == import).Children, c => c.Node == scan);
    }

    [Fact]
    public void AddScanNode_Does_Not_Nest_Under_Selected_Scan()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        var scan1 = new SceneNode { Name = "Scan 12-00-01", Selectable = true, PickTier = PickTier.Content };
        var scan2 = new SceneNode { Name = "Scan 12-00-02", Selectable = true, PickTier = PickTier.Content };

        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddScanNode(scan1);
        vm.GetSelectedSceneNode = () => scan1;
        vm.AddScanNode(scan2);

        var rotary = vm.OutlinerItems.First(i => i.Name == "Rotary Bed");
        Assert.Equal(2, rotary.Children.Count);
        Assert.All(rotary.Children, c => Assert.True(c.Node.Name.StartsWith("Scan ", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void FlattenScansToBedGroup_Moves_Nested_Scans_To_Bed_Level()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        var import = new SceneNode { Name = "part.glb", Selectable = true };
        var scan = new SceneNode { Name = "Scan 12-00-00", Selectable = true, PickTier = PickTier.Content };

        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddImportNode(import);

        var rotary = vm.OutlinerItems.First(i => i.Name == "Rotary Bed");
        var importItem = rotary.Children.First(c => c.Node == import);
        importItem.AddChild(new OutlinerItemViewModel(
            scan,
            () => { },
            _ => { },
            canDelete: true));

        vm.FlattenScansToBedGroup();

        Assert.DoesNotContain(importItem.Children, c => c.Node == scan);
        Assert.Contains(rotary.Children, c => c.Node == scan);
    }

    [Fact]
    public void Shift_Click_Selects_Scan_Range_Under_Rotary_Bed()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");

        for (int i = 0; i < 4; i++)
            vm.AddScanNode(new SceneNode { Name = $"Scan 12-00-0{i}", Selectable = true, PickTier = PickTier.Content });

        var scans = vm.GetBedLevelScanItems();
        vm.OnOutlinerScanClicked(scans[1], shiftHeld: false, ctrlHeld: false);
        vm.OnOutlinerScanClicked(scans[3], shiftHeld: true, ctrlHeld: false);

        Assert.Equal(3, vm.SelectedScanCount);
        Assert.True(scans[1].IsScanMultiSelected);
        Assert.True(scans[2].IsScanMultiSelected);
        Assert.True(scans[3].IsScanMultiSelected);
        Assert.False(scans[0].IsScanMultiSelected);
    }

    [Fact]
    public void Plain_Click_Selects_Only_Scan_Row_Not_Bed()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddScanNode(new SceneNode { Name = "Scan 12-00-01", Selectable = true, PickTier = PickTier.Content });

        var rotary = vm.OutlinerItems.First(i => i.Name == "Rotary Bed");
        var scan = vm.GetBedLevelScanItems().Single();

        vm.OnOutlinerScanClicked(scan, shiftHeld: false, ctrlHeld: false);
        vm.SetOutlinerSelection(scan.Node);

        Assert.True(scan.IsScanMultiSelected);
        Assert.False(rotary.IsOutlinerSelected);
        Assert.False(rotary.IsScanMultiSelected);
    }

    [Fact]
    public void Ctrl_Click_Toggles_Scan_Multi_Selection()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");

        vm.AddScanNode(new SceneNode { Name = "Scan 12-00-01", Selectable = true, PickTier = PickTier.Content });
        vm.AddScanNode(new SceneNode { Name = "Scan 12-00-02", Selectable = true, PickTier = PickTier.Content });

        var scans = vm.GetBedLevelScanItems();
        vm.OnOutlinerScanClicked(scans[0], shiftHeld: false, ctrlHeld: false);
        vm.OnOutlinerScanClicked(scans[1], shiftHeld: false, ctrlHeld: true);

        Assert.Equal(2, vm.SelectedScanCount);
        Assert.True(scans[0].IsScanMultiSelected);
        Assert.True(scans[1].IsScanMultiSelected);

        vm.OnOutlinerScanClicked(scans[0], shiftHeld: false, ctrlHeld: true);
        Assert.Equal(1, vm.SelectedScanCount);
        Assert.False(scans[0].IsScanMultiSelected);
        Assert.True(scans[1].IsScanMultiSelected);
    }

    [Fact]
    public void RefreshScanSelectionVisuals_Highlights_Root_Level_Scan()
    {
        var vm = new ViewportViewModel();
        var scan = new SceneNode { Name = "Scan 10-05-13", Selectable = true, PickTier = PickTier.Content };
        vm.OutlinerItems.Add(new OutlinerItemViewModel(scan, () => { }, _ => { }));

        var item = vm.OutlinerItems.Single();
        vm.OnOutlinerScanClicked(item, shiftHeld: false, ctrlHeld: false);

        Assert.True(item.IsRowHighlighted);
        Assert.True(item.IsScanMultiSelected);
    }

    [Fact]
    public void FindOutlinerItemForSelection_Matches_By_Name_When_Reference_Differs()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        var scanA = new SceneNode { Name = "Scan 10-05-13", Selectable = true, PickTier = PickTier.Content };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddScanNode(scanA);

        var pickClone = new SceneNode { Name = "Scan 10-05-13", Selectable = true, PickTier = PickTier.Content };
        var item = vm.FindOutlinerItemForSelection(pickClone);

        Assert.NotNull(item);
        Assert.Equal("Scan 10-05-13", item!.Name);
    }

    [Fact]
    public void RefreshCanMergeScans_Fires_ScanSelectionChanged()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");

        vm.AddScanNode(new SceneNode { Name = "Scan 12-00-01", Selectable = true, PickTier = PickTier.Content });
        vm.AddScanNode(new SceneNode { Name = "Scan 12-00-02", Selectable = true, PickTier = PickTier.Content });

        int calls = 0;
        vm.OnScanSelectionChanged = () => calls++;

        var scans = vm.GetBedLevelScanItems();
        vm.OnOutlinerScanClicked(scans[0], shiftHeld: false, ctrlHeld: false);
        vm.OnOutlinerScanClicked(scans[1], shiftHeld: false, ctrlHeld: true);

        Assert.True(calls >= 2);
        Assert.Equal(2, vm.SelectedScanCount);
        Assert.True(vm.CanMergeScans);
    }

    [Fact]
    public void SetOutlinerSelection_Preserves_Multi_Select()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");

        vm.AddScanNode(new SceneNode { Name = "Scan 12-00-01", Selectable = true, PickTier = PickTier.Content });
        vm.AddScanNode(new SceneNode { Name = "Scan 12-00-02", Selectable = true, PickTier = PickTier.Content });

        var scans = vm.GetBedLevelScanItems();
        vm.OnOutlinerScanClicked(scans[0], shiftHeld: false, ctrlHeld: false);
        vm.OnOutlinerScanClicked(scans[1], shiftHeld: false, ctrlHeld: true);

        vm.SetOutlinerSelection(scans[0].Node);

        Assert.Equal(2, vm.SelectedScanCount);
        Assert.True(vm.CanMergeScans);
        Assert.True(scans[0].IsScanMultiSelected);
        Assert.True(scans[1].IsScanMultiSelected);
    }

    [Fact]
    public void FindOutlinerItemForSelection_Returns_Null_For_Ambiguous_Name()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");

        vm.AddScanNode(new SceneNode { Name = "Scan 10-12-24", Selectable = true, PickTier = PickTier.Content });
        vm.AddScanNode(new SceneNode { Name = "Scan 10-12-24", Selectable = true, PickTier = PickTier.Content });

        var pickClone = new SceneNode { Name = "Scan 10-12-24", Selectable = true, PickTier = PickTier.Content };
        Assert.Null(vm.FindOutlinerItemForSelection(pickClone));
    }

    [Fact]
    public void FindUserMeshOutlinerItem_Resolves_Nested_Scan()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        var import = new SceneNode { Name = "part.glb", Selectable = true };
        var scan = new SceneNode { Name = "Scan 12-00-00", Selectable = true, PickTier = PickTier.Content };

        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddImportNode(import);

        var rotary = vm.OutlinerItems.First(i => i.Name == "Rotary Bed");
        rotary.Children.First(c => c.Node == import)
            .AddChild(new OutlinerItemViewModel(scan, () => { }, _ => { }));

        Assert.Same(rotary.Children.First(c => c.Node == import).Children.Single(), vm.FindUserMeshOutlinerItem(scan));
    }
}