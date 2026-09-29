using System.Numerics;
using MassiveSlicer.App.Enums;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public sealed class OutlinerToolpathKindTest
{
    [Fact]
    public void Print_toolpath_uses_printer_icon_and_green()
    {
        var vm = new ViewportViewModel();
        var import = new SceneNode { Name = "Part", PendingMesh = BoxMesh() };
        vm.AddImportNode(import);
        var importItem = vm.EnumerateUserModelItems().Single();
        var toolpath = new SceneNode { Name = "Part", Selectable = true };
        vm.RegisterToolpathInOutliner(toolpath, importItem, OutlinerToolpathKind.Print);

        var item = importItem.Children.Single();
        Assert.True(item.IsToolpath);
        Assert.True(item.IsPrintToolpath);
        Assert.False(item.IsMillToolpath);
        Assert.Equal(OutlinerToolpathKinds.PrintIcon, item.TypeIcon);
        Assert.Equal(OutlinerToolpathKinds.PrintColor, item.TypeColor);
        Assert.Equal(OutlinerToolpathKinds.PrintTip, item.TypeTip);
    }

    [Fact]
    public void Mill_toolpath_uses_mill_icon_and_orange()
    {
        var vm = new ViewportViewModel();
        var import = new SceneNode { Name = "Part", PendingMesh = BoxMesh() };
        vm.AddImportNode(import);
        var importItem = vm.EnumerateUserModelItems().Single();
        var toolpath = new SceneNode { Name = "Part Mill", Selectable = true };
        vm.RegisterToolpathInOutliner(toolpath, importItem, OutlinerToolpathKind.Mill);

        var item = importItem.Children.Single();
        Assert.True(item.IsMillToolpath);
        Assert.False(item.IsPrintToolpath);
        Assert.Equal(OutlinerToolpathKinds.MillIcon, item.TypeIcon);
        Assert.Equal(OutlinerToolpathKinds.MillColor, item.TypeColor);
        Assert.Equal(OutlinerToolpathKinds.MillTip, item.TypeTip);
        Assert.NotEqual(OutlinerToolpathKinds.PrintColor, item.TypeColor);
        Assert.NotEqual(OutlinerToolpathKinds.PrintIcon, item.TypeIcon);
    }

    [Fact]
    public void FindToolpathChild_does_not_return_print_when_looking_for_mill()
    {
        var vm = new ViewportViewModel();
        var import = new SceneNode { Name = "Part", PendingMesh = BoxMesh() };
        vm.AddImportNode(import);
        var importItem = vm.EnumerateUserModelItems().Single();
        vm.RegisterToolpathInOutliner(new SceneNode { Name = "Part" }, importItem, OutlinerToolpathKind.Print);

        Assert.Null(ViewportViewModel.FindToolpathChild(importItem, OutlinerToolpathKind.Mill));
        Assert.NotNull(ViewportViewModel.FindToolpathChild(importItem, OutlinerToolpathKind.Print));
    }

    [Fact]
    public void Cell_swap_keeps_existing_toolpath_and_forks_a_new_one()
    {
        var vm = new ViewportViewModel();
        var import = new SceneNode { Name = "Scene 8 P04", PendingMesh = BoxMesh() };
        vm.AddImportNode(import);
        var importItem = vm.EnumerateUserModelItems().Single();
        vm.RegisterToolpathInOutliner(new SceneNode { Name = "Scene 8 P04" }, importItem, OutlinerToolpathKind.Print);

        ViewportViewModel.KeepExistingToolpathsOnReslice(importItem);

        var kept = ViewportViewModel.FindToolpathChild(importItem, OutlinerToolpathKind.Print);
        Assert.NotNull(kept);
        Assert.True(kept!.KeepOnReslice);
        Assert.Null(ViewportViewModel.FindUpdatableToolpathChild(importItem, OutlinerToolpathKind.Print));

        vm.RegisterToolpathInOutliner(
            new SceneNode { Name = ViewportViewModel.UniqueSiblingToolpathName(importItem, "Scene 8 P04") },
            importItem,
            OutlinerToolpathKind.Print);

        Assert.Equal(2, importItem.Children.Count(c => c.IsPrintToolpath));
        Assert.Equal("Scene 8 P04", kept.Name);
        var next = ViewportViewModel.FindUpdatableToolpathChild(importItem, OutlinerToolpathKind.Print);
        Assert.NotNull(next);
        Assert.Equal("Scene 8 P04 2", next!.Name);
        Assert.False(next.KeepOnReslice);
        Assert.True(kept.KeepOnReslice);
    }

    [Fact]
    public void Infer_detects_mill_from_moves_and_name()
    {
        var millTp = new Toolpath();
        var layer = new ToolpathLayer(0, 1f);
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(1, 0, 0), MoveKind.Mill));
        millTp.Layers.Add(layer);

        Assert.Equal(OutlinerToolpathKind.Mill, OutlinerToolpathKinds.Infer("finish", millTp));
        Assert.Equal(OutlinerToolpathKind.Mill, OutlinerToolpathKinds.Infer("Relief Mill D12", null));
        Assert.Equal(OutlinerToolpathKind.Print, OutlinerToolpathKinds.Infer("Part", null));
        Assert.Equal("Mill", OutlinerToolpathKinds.ToWorkspaceValue(OutlinerToolpathKind.Mill));
        Assert.Equal(OutlinerToolpathKind.Mill, OutlinerToolpathKinds.Parse("mill"));
        Assert.Equal(OutlinerToolpathKind.Print, OutlinerToolpathKinds.Parse(null));

        var printTp = new Toolpath();
        var printLayer = new ToolpathLayer(0, 1f);
        printLayer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(1, 0, 0), MoveKind.Extrude));
        printTp.Layers.Add(printLayer);
        Assert.Equal(OutlinerToolpathKind.Print, OutlinerToolpathKinds.Infer("KRL: job", printTp));
    }

    private static MeshData BoxMesh()
    {
        var positions = new[]
        {
            new OpenTK.Mathematics.Vector3(0, 0, 0),
            new OpenTK.Mathematics.Vector3(1, 0, 0),
            new OpenTK.Mathematics.Vector3(1, 1, 0),
            new OpenTK.Mathematics.Vector3(0, 1, 0),
        };
        uint[] indices = [0, 1, 2, 0, 2, 3];
        return new MeshData(positions, positions, indices, "box");
    }
}
