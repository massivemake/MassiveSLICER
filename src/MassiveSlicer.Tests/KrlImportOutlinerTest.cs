using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public sealed class KrlImportOutlinerTest
{
    private static Toolpath MinimalToolpath()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f);
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(10, 0, 0), MoveKind.Travel));
        tp.Layers.Add(layer);
        return tp;
    }

    [Fact]
    public void AddImportedToolpath_Nests_Under_RotaryBed_When_No_Print_Object()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");

        vm.AddImportedToolpath(MinimalToolpath(), "KRL: test_program");

        var rotary = vm.OutlinerItems.First(i => i.Name == "Rotary Bed");
        Assert.Single(rotary.Children);
        Assert.Equal("KRL: test_program", rotary.Children[0].Name);
        Assert.DoesNotContain(vm.OutlinerItems, i => i.Name == "KRL: test_program");
    }

    [Fact]
    public void AddImportedToolpath_Nests_Under_Active_Print_Object_When_Present()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        var import = new SceneNode { Name = "part.glb", Selectable = true };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddImportNode(import);
        vm.GetSelectedSceneNode = () => import;

        vm.AddImportedToolpath(MinimalToolpath(), "KRL: test_program");

        var importItem = vm.OutlinerItems.First(i => i.Name == "Rotary Bed").Children.First(c => c.Node == import);
        Assert.Single(importItem.Children);
        Assert.Equal("KRL: test_program", importItem.Children[0].Name);
    }

    [Fact]
    public void AddImportedToolpath_Print_kind_is_print_not_mill()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");

        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f);
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(10, 0, 0), MoveKind.Extrude));
        tp.Layers.Add(layer);
        vm.AddImportedToolpath(tp, "KRL: print_job", kind: MassiveSlicer.App.Enums.OutlinerToolpathKind.Print);

        var item = vm.OutlinerItems.First(i => i.Name == "Rotary Bed").Children.Single();
        Assert.True(item.IsPrintToolpath);
        Assert.False(item.IsMillToolpath);
        Assert.Equal(MassiveSlicer.App.Enums.OutlinerToolpathKinds.PrintTip, item.TypeTip);
    }

    [Fact]
    public void SetRotaryBedGroup_null_promotes_krl_import_to_outliner_root()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddImportedToolpath(MinimalToolpath(), "KRL: test_program");

        Assert.DoesNotContain(vm.OutlinerItems, i => i.Name == "KRL: test_program");

        vm.SetRotaryBedGroup(null, "Rotary Bed");

        Assert.DoesNotContain(vm.OutlinerItems, i => i.Name == "Rotary Bed");
        Assert.Contains(vm.OutlinerItems, i => i.Name == "KRL: test_program");
        var item = vm.OutlinerItems.Single(i => i.Name == "KRL: test_program");
        Assert.True(item.IsToolpath);
    }

    [Fact]
    public void SetRotaryBedGroup_new_pivot_keeps_krl_import_under_rotary()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddImportedToolpath(MinimalToolpath(), "KRL: test_program");

        var next = new SceneNode { Name = "RotaryBed_Top2", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(next, "Rotary Bed");

        var rotary = vm.OutlinerItems.First(i => i.Name == "Rotary Bed");
        Assert.Equal(next, rotary.Node);
        Assert.Single(rotary.Children);
        Assert.Equal("KRL: test_program", rotary.Children[0].Name);
        Assert.DoesNotContain(vm.OutlinerItems, i => i.Name == "KRL: test_program");
    }

    [Fact]
    public void EnumerateToolpathItems_includes_standalone_krl_under_rotary()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddImportedToolpath(MinimalToolpath(), "KRL: test_program");

        var items = vm.EnumerateToolpathItems().ToList();
        Assert.Single(items);
        Assert.Equal("KRL: test_program", items[0].Name);
        Assert.True(items[0].IsToolpath);
    }
}