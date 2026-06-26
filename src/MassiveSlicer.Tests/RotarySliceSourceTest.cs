using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class RotarySliceSourceTest
{
    [Fact]
    public void FindUserMeshOutlinerItem_Resolves_Import_Not_Rotary_Group()
    {
        var vm = new ViewportViewModel();

        var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
        var bedMesh = new SceneNode
        {
            Name = "rotary top",
            Selectable = false,
            PickTier = PickTier.Environment,
            PendingMesh = new MeshData(
                [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                [0u, 1u, 2u],
                "bed"),
        };
        var import = new SceneNode
        {
            Name = "part.glb",
            Selectable = true,
            PickTier = PickTier.Content,
            PendingMesh = new MeshData(
                [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                [0u, 1u, 2u],
                "part"),
        };
        pivot.AddChild(bedMesh);
        pivot.AddChild(import);

        vm.SetRotaryBedGroup(pivot, "Rotary Bed");
        vm.AddImportNode(import);

        var found = vm.FindUserMeshOutlinerItem(import);
        Assert.NotNull(found);
        Assert.Same(import, found!.Node);
        Assert.NotSame(pivot, found.Node);
    }
}