using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class TcpAttractorDeleteTest
{
    [Fact]
    public void Delete_under_the_rotary_bed_removes_the_handle_and_leaves_the_bed()
    {
        var vm = new ViewportViewModel();
        var pivot = new SceneNode { Name = "RotaryBed_Top" };
        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddTcpAttractor();
        vm.AddTcpDeflector();
        var attractor = Assert.Single(vm.TcpAttractors);
        var deflector = Assert.Single(vm.TcpDeflectors);
        pivot.AddChild(attractor.Node);
        pivot.AddChild(deflector.Node);

        vm.RequestDeleteNode(attractor.Node);
        vm.RequestDeleteNode(deflector.Node);

        Assert.Empty(vm.TcpAttractors);
        Assert.Empty(vm.TcpDeflectors);
        Assert.Empty(vm.CaptureTcpAdjustments());
        Assert.Contains(vm.OutlinerItems, i => i.Name == "Rotary Bed");
        Assert.DoesNotContain(vm.OutlinerItems, i => ReferenceEquals(i, attractor.Outliner));
        Assert.DoesNotContain(vm.OutlinerItems, i => ReferenceEquals(i, deflector.Outliner));
        Assert.Contains(attractor.Node, vm.PendingRemoveNodes);
        Assert.Contains(deflector.Node, vm.PendingRemoveNodes);
    }

    [Fact]
    public void Outliner_trash_removes_an_attractor_and_a_deflector()
    {
        var vm = new ViewportViewModel();
        vm.AddTcpAttractor();
        vm.AddTcpDeflector();
        var attractor = Assert.Single(vm.TcpAttractors);
        var deflector = Assert.Single(vm.TcpDeflectors);

        attractor.Outliner.DeleteCommand.Execute(null);
        deflector.Outliner.DeleteCommand.Execute(null);

        Assert.Empty(vm.TcpAttractors);
        Assert.Empty(vm.TcpDeflectors);
        Assert.Empty(vm.CaptureTcpAdjustments());
    }
}
