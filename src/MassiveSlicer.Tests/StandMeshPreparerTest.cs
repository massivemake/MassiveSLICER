using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public class StandMeshPreparerTest
{
    private static string RepoRoot =>
        AssetPaths.FindCellsDirectory() is { } cells
            ? Path.GetFullPath(Path.Combine(cells, "..", ".."))
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData("assets/cells/LFAM3/Stands/stand_extruder.glb")]
    [InlineData("assets/cells/LFAM3/Stands/stand_scanner.glb")]
    [InlineData("assets/cells/LFAM3/Stands/stand_spindle.glb")]
    public void Stand_cleanup_reduces_triangle_count(string rel)
    {
        Directory.SetCurrentDirectory(RepoRoot);
        var path = AssetPaths.Resolve(rel);
        var root = GltfLoader.LoadNativeMeters(path);
        var stats = StandMeshPreparer.OptimizeSubtree(root);
        System.Console.WriteLine(
            $"{Path.GetFileName(rel)}: {stats.BeforeTriangles:N0} → {stats.AfterTriangles:N0} tris ({stats.Meshes} meshes)");

        Assert.True(stats.AfterTriangles <= stats.BeforeTriangles);
        Assert.True(stats.AfterTriangles > 0);
    }

    [Theory]
    [InlineData("assets/cells/LFAM3/Toolheads/spindle.glb")]
    [InlineData("assets/cells/LFAM3/Toolheads/scanner.glb")]
    public void Scene_glb_cleanup_reduces_triangle_count(string rel)
    {
        Directory.SetCurrentDirectory(RepoRoot);
        var path = AssetPaths.Resolve(rel);
        var root = GltfLoader.Load(path);
        var stats = StandMeshPreparer.OptimizeSubtree(root, StandMeshPreparer.DefaultSceneGlbOptions);
        System.Console.WriteLine(
            $"{Path.GetFileName(rel)}: {stats.BeforeTriangles:N0} → {stats.AfterTriangles:N0} tris ({stats.Meshes} meshes)");

        Assert.True(stats.AfterTriangles <= stats.BeforeTriangles);
        Assert.True(stats.AfterTriangles > 0);
    }
}