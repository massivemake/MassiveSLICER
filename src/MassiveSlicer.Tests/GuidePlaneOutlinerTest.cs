using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public sealed class GuidePlaneOutlinerTest
{
    [Fact]
    public void Planes_group_is_a_child_of_the_mesh()
    {
        var vm = new ViewportViewModel();
        var mesh = new SceneNode { Name = "Panel", PendingMesh = Box() };
        vm.AddImportNode(mesh);
        var item = vm.EnumerateUserModelItems().Single();

        var rows = new MultiPlanarPlaneRow[] { new(0, 10, 0), new(100, 0, 20) };
        vm.EnsureGuidePlaneOutliner(item, rows, 400f);

        var group = Assert.Single(item.Children, c => c.IsPlanesGroup);
        Assert.Equal("Planes", group.Name);
        Assert.Equal(2, group.Children.Count);
        Assert.All(group.Children, c => Assert.True(c.IsGuidePlane));
        Assert.Contains("Y 10.0", group.Children[0].Name);
        Assert.Contains("X 20.0", group.Children[1].Name);
        Assert.True(group.Node.IsAuthoringOverlay);
        Assert.DoesNotContain(group, vm.EnumerateUserModelItems());
        var plane = group.Children[0];
        Assert.False(plane.Node.PickIgnore);
        Assert.Null(vm.FindUserMeshOutlinerItem(plane.Node));
        Assert.Same(plane, vm.FindOutlinerItemForSelection(plane.Node));
        Assert.Same(item, vm.OwningModelItem(plane));
    }

    static MeshData Box()
    {
        var p = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var n = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        return new MeshData(p, n, null, "box");
    }
}
