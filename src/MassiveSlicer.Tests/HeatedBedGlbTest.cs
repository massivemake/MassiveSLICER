using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

/// <summary>
/// SB101 Preview: heated plate looked torn vs Drive Cell 3D.
/// Cause: lfam3_HeatedBed.glb prim 0 is a 5-vert LINE_STRIP outline (no indices).
/// GltfLoader uploaded it as triangles → one diagonal tent across the plate.
/// Drive's Three.js keeps it as lines, so the same GLB looks fine there.
/// </summary>
public class HeatedBedGlbTest
{
    static string GlbPath()
    {
        var rel = "assets/cells/LFAM3/lfam3_HeatedBed.glb";
        var resolved = AssetPaths.Resolve(rel);
        Assert.True(File.Exists(resolved), resolved);
        return resolved;
    }

    [Fact]
    public void Heated_bed_glb_does_not_load_line_strip_outline_as_triangles()
    {
        var root = GltfLoader.Load(GlbPath());
        int meshes = 0;
        int unindexed = 0;
        long indexedTris = 0;
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } m) continue;
            meshes++;
            if (m.Indices is not { Length: > 0 } idx)
                unindexed++;
            else
                indexedTris += idx.Length / 3;
        }

        Assert.Equal(0, unindexed);
        Assert.Equal(2, meshes);
        Assert.Equal(724, indexedTris); // 1296/3 + 876/3
    }

    [Fact]
    public void Heated_bed_merge_keeps_only_solid_triangles()
    {
        var root = GltfLoader.Load(GlbPath());
        var before = SceneTriangleStats.Count(root);
        var stats = SceneMeshMerger.MergeSubtree(root, "heated_merged");
        Assert.Equal(2, stats.SourceMeshes);
        Assert.Equal(before.Triangles, stats.Triangles);
        Assert.Equal(724, stats.Triangles);
        Assert.Equal(1, SceneTriangleStats.Count(root).Meshes);
    }
}
