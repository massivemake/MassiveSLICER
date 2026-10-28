using MassiveSlicer.App.Enums;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

/// <summary>
/// Deleting a rotary-bed model used to drop nested rows and leave the toolpath drawn.
/// Anything still drawn has to be a deletable outliner row.
/// </summary>
public sealed class CreatedItemOutlinerDeleteTest
{
    static ViewportViewModel RotaryVm()
    {
        var vm = new ViewportViewModel();
        vm.SetRotaryBedGroup(new SceneNode { Name = "RotaryBed_Top" }, "Rotary Bed");
        return vm;
    }

    static OutlinerItemViewModel Import(ViewportViewModel vm, string name)
    {
        var node = new SceneNode { Name = name, Selectable = true };
        vm.AddImportNode(node);
        return vm.OutlinerItems.First(i => i.Name == "Rotary Bed").Children.First(c => c.Node == node);
    }

    [Fact]
    public void Rotary_model_trash_removes_its_mill_toolpath()
    {
        var vm = RotaryVm();
        var model = Import(vm, "cnc_drilling_test");
        var toolpath = new SceneNode { Name = "cnc_drilling_test Mill", Selectable = true };
        vm.RegisterToolpathInOutliner(toolpath, model, OutlinerToolpathKind.Mill);

        model.DeleteCommand.Execute(null);

        Assert.Contains(toolpath, vm.PendingRemoveNodes);
        Assert.Contains(model.Node, vm.PendingRemoveNodes);
        Assert.Null(vm.FindOutlinerItem(toolpath));
        Assert.Null(vm.FindOutlinerItem(model.Node));
        Assert.Equal(0, vm.AdoptUnlistedToolpaths([toolpath]));
        Assert.Null(vm.FindOutlinerItem(toolpath));
    }

    [Fact]
    public void Scan_trash_removes_a_nested_toolpath()
    {
        var vm = RotaryVm();
        var scan = new SceneNode { Name = "Scan 12-00-00", Selectable = true, PickTier = PickTier.Content };
        vm.AddScanNode(scan);
        var scanItem = vm.OutlinerItems.First(i => i.Name == "Rotary Bed").Children.First(c => c.Node == scan);
        var toolpath = new SceneNode { Name = "Scan Mill", Selectable = true };
        vm.RegisterToolpathInOutliner(toolpath, scanItem, OutlinerToolpathKind.Mill);

        scanItem.DeleteCommand.Execute(null);

        Assert.Contains(toolpath, vm.PendingRemoveNodes);
        Assert.Contains(scan, vm.PendingRemoveNodes);
        Assert.Null(vm.FindOutlinerItem(toolpath));
    }

    [Fact]
    public void Rotary_model_trash_removes_its_modifier()
    {
        var vm = RotaryVm();
        var model = Import(vm, "part");
        var cut = vm.AddCutModifier(model);
        var plane = vm.GetModifierGizmoNode(cut);
        Assert.NotNull(plane);

        model.DeleteCommand.Execute(null);

        Assert.Contains(plane, vm.PendingRemoveNodes);
        Assert.Contains(model.Node, vm.PendingRemoveNodes);
        Assert.Null(vm.FindOutlinerItem(plane));
        Assert.Null(vm.GetModifierGizmoNode(cut));
    }

    [Fact]
    public void Modifier_row_itself_can_be_deleted()
    {
        var vm = RotaryVm();
        var model = Import(vm, "part");
        var cut = vm.AddCutModifier(model);
        var plane = vm.GetModifierGizmoNode(cut);
        var group = model.Children.First(c => c.IsModifiersGroup);
        var row = group.Children.First(c => c.Node == plane);
        Assert.True(row.CanDelete);

        row.DeleteCommand.Execute(null);

        Assert.Contains(plane!, vm.PendingRemoveNodes);
        Assert.DoesNotContain(group.Children, c => c.Node == plane);
        Assert.Null(vm.GetModifierGizmoNode(cut));
        Assert.NotNull(vm.FindOutlinerItem(model.Node));
    }

    [Fact]
    public void Unlisted_toolpath_is_adopted_and_can_be_deleted()
    {
        var vm = RotaryVm();
        var toolpath = new SceneNode { Name = "cnc_drilling_test Mill", Selectable = true };

        Assert.Equal(1, vm.AdoptUnlistedToolpaths([toolpath]));
        Assert.Equal(0, vm.AdoptUnlistedToolpaths([toolpath]));

        var row = vm.FindOutlinerItem(toolpath);
        Assert.NotNull(row);
        Assert.True(row!.CanDelete);
        Assert.True(row.IsMillToolpath);
        var rotary = vm.OutlinerItems.First(i => i.Name == "Rotary Bed");
        Assert.Contains(rotary.Children, c => c.Node == toolpath);

        row.DeleteCommand.Execute(null);

        Assert.Contains(toolpath, vm.PendingRemoveNodes);
        Assert.Null(vm.FindOutlinerItem(toolpath));
    }

    [Fact]
    public void Pieces_group_trash_removes_each_piece_toolpath()
    {
        var vm = new ViewportViewModel();
        var group = vm.CreateAppliedPiecesGroupNamed("Wall");
        var piece = new SceneNode { Name = "Wall 01", Selectable = true };
        var pieceItem = vm.AddRestoredPieceToGroup(piece, group);
        var toolpath = new SceneNode { Name = "Wall 01", Selectable = true };
        vm.RegisterToolpathInOutliner(toolpath, pieceItem, OutlinerToolpathKind.Print);

        group.DeleteCommand.Execute(null);

        Assert.Contains(toolpath, vm.PendingRemoveNodes);
        Assert.Contains(piece, vm.PendingRemoveNodes);
        Assert.Null(vm.FindOutlinerItem(toolpath));
        Assert.Null(vm.FindOutlinerItem(piece));
    }

    [Fact]
    public void Toolpath_row_deletes_without_removing_the_model()
    {
        var vm = RotaryVm();
        var model = Import(vm, "part");
        var toolpath = new SceneNode { Name = "part Mill", Selectable = true };
        vm.RegisterToolpathInOutliner(toolpath, model, OutlinerToolpathKind.Mill);
        var row = vm.FindOutlinerItem(toolpath);
        Assert.NotNull(row);

        row!.DeleteCommand.Execute(null);

        Assert.Contains(toolpath, vm.PendingRemoveNodes);
        Assert.DoesNotContain(model.Node, vm.PendingRemoveNodes);
        Assert.NotNull(vm.FindOutlinerItem(model.Node));
        Assert.Null(vm.FindOutlinerItem(toolpath));
    }
}
