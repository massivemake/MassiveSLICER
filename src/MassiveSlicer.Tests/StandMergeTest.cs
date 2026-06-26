using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public class StandMergeTest
{
    private static string RepoRoot =>
        AssetPaths.FindCellsDirectory() is { } cells
            ? Path.GetFullPath(Path.Combine(cells, "..", ".."))
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData("assets/cells/LFAM3/Stands/stand_extruder.glb")]
    [InlineData("assets/cells/LFAM3/Stands/stand_scanner.glb")]
    [InlineData("assets/cells/LFAM3/Stands/stand_spindle.glb")]
    public void Stand_merge_collapses_to_one_draw_call(string rel)
    {
        Directory.SetCurrentDirectory(RepoRoot);
        var root = GltfLoader.LoadNativeMeters(AssetPaths.Resolve(rel));
        StandMeshPreparer.OptimizeSubtree(root);

        var before = SceneTriangleStats.Count(root);
        var stats  = SceneMeshMerger.MergeSubtree(root, "merged");

        Assert.True(stats.SourceMeshes > 1, $"{rel}: expected multiple source meshes");
        Assert.Equal(before.Triangles, stats.Triangles);

        var after = SceneTriangleStats.Count(root);
        Assert.Equal(1, after.Meshes);
        Assert.Equal(before.Triangles, after.Triangles);

        System.Console.WriteLine(
            $"{Path.GetFileName(rel)}: {stats.SourceMeshes} meshes → 1 draw, {stats.Triangles:N0} tris");
    }
}