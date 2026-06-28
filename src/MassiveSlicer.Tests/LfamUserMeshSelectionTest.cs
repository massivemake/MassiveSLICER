using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class LfamUserMeshSelectionTest
{
    [Fact]
    public void IsUserModelSceneNode_Resolves_Import_Parented_Under_Bed()
    {
        var vm = new ViewportViewModel();

        var bed = new SceneNode
        {
            Name = "bed",
            Selectable = false,
            PickTier = PickTier.Environment,
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
        var childMesh = new SceneNode { Name = "child", Selectable = true };
        import.AddChild(childMesh);
        bed.AddChild(import);

        vm.AddImportNode(import);

        Assert.True(vm.IsUserModelSceneNode(import));
        Assert.True(vm.IsUserModelSceneNode(childMesh));
        Assert.False(vm.IsUserModelSceneNode(bed));
    }
}