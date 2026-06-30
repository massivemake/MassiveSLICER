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

    [Fact]
    public void IsUserModelSceneNode_Resolves_Import_With_Print_Bed_In_Cell_Outliner()
    {
        var vm = new ViewportViewModel();

        var bed = new SceneNode
        {
            Name = "lfam1_bed_Root",
            Selectable = false,
            PickTier = PickTier.Environment,
        };
        var import = new SceneNode
        {
            Name = "bracket.glb",
            Selectable = true,
            PickTier = PickTier.Content,
            PendingMesh = new MeshData(
                [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                [0u, 1u, 2u],
                "bracket"),
        };
        var meshLeaf = new SceneNode { Name = "Mesh_0", Selectable = true };

        import.AddChild(meshLeaf);
        bed.AddChild(import);

        vm.SetCellEnvironmentOutliner([(bed, "Print Bed")]);
        vm.AddImportNode(import);

        Assert.True(vm.IsUserModelSceneNode(import));
        Assert.True(vm.IsUserModelSceneNode(meshLeaf));
        Assert.False(vm.IsUserModelSceneNode(bed));
    }

    [Fact]
    public void User_import_subtree_stays_content_tier_when_parented_under_bed()
    {
        var bed = new SceneNode
        {
            Name = "lfam1_bed_Root",
            Selectable = false,
            PickTier = PickTier.Environment,
        };
        var import = new SceneNode
        {
            Name = "widget.stp",
            PendingMesh = new MeshData(
                [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                [0u, 1u, 2u],
                "widget"),
        };
        var meshLeaf = new SceneNode { Name = "Mesh_0" };
        import.AddChild(meshLeaf);
        bed.AddChild(import);

        foreach (var n in import.SelfAndDescendants())
        {
            n.Selectable = true;
            n.PickTier = PickTier.Content;
        }

        Assert.True(import.Selectable);
        Assert.Equal(PickTier.Content, import.PickTier);
        Assert.True(meshLeaf.Selectable);
        Assert.Equal(PickTier.Content, meshLeaf.PickTier);
        Assert.False(bed.Selectable);
    }
}