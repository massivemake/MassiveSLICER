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
}