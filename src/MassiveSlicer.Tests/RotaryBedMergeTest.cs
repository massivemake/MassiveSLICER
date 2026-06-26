using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public class RotaryBedMergeTest
{
    private static string RepoRoot =>
        AssetPaths.FindCellsDirectory() is { } cells
            ? Path.GetFullPath(Path.Combine(cells, "..", ".."))
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData("assets/cells/LFAM3/rotary_bed_bottom.glb")]
    [InlineData("assets/cells/LFAM3/rotary_bed_top.glb")]
    public void Rotary_merge_collapses_to_one_draw_call(string rel)
    {
        Directory.SetCurrentDirectory(RepoRoot);
        var root = GltfLoader.Load(AssetPaths.Resolve(rel));
        var before = SceneTriangleStats.Count(root);
        var stats  = SceneMeshMerger.MergeSubtree(root, "merged");

        // GLBs may ship pre-merged (single geo), so >=1 source; the point is one draw call after.
        Assert.True(stats.SourceMeshes >= 1);
        Assert.Equal(before.Triangles, stats.Triangles);

        var after = SceneTriangleStats.Count(root);
        Assert.Equal(1, after.Meshes);
        Assert.Equal(before.Triangles, after.Triangles);
    }
}