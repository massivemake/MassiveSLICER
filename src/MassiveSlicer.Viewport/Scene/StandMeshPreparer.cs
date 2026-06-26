namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Applies <see cref="MeshCleanup"/> to LFAM cell environment GLBs (heavy CAD shells with duplicates).
/// </summary>
public static class StandMeshPreparer
{
    /// <summary>
    /// Aggressive dedup for environment stands. Skips slow gap/colinear passes.
    /// </summary>
    public static readonly MeshCleanupOptions DefaultStandOptions = new()
    {
        MergeVertices                = true,
        UnifyPolygons                = true,
        ForceUnify                   = true,
        RemoveFloatingVertices       = true,
        RemoveOnePointPolygons       = true,
        RemoveTwoPointPolygons       = true,
        FixDuplicatePointsInPolygons = true,
        FixFaceNormalVectors         = true,
        RemoveColinearVertices       = false,
        FixGaps                      = false,
        MergeEpsilon                 = 0.001f, // metres — 1 mm weld for native-metre stands
    };

    /// <summary>
    /// Same dedup profile for scene-frame GLBs loaded via <c>GltfLoader.Load</c>
    /// (rotary bed, spindle/scanner toolheads). Vertex positions are still GLTF metres.
    /// </summary>
    public static readonly MeshCleanupOptions DefaultSceneGlbOptions = new()
    {
        MergeVertices                = true,
        UnifyPolygons                = true,
        ForceUnify                   = true,
        RemoveFloatingVertices       = true,
        RemoveOnePointPolygons       = true,
        RemoveTwoPointPolygons       = true,
        FixDuplicatePointsInPolygons = true,
        FixFaceNormalVectors         = true,
        RemoveColinearVertices       = false,
        FixGaps                      = false,
        MergeEpsilon                 = 0.001f, // metres — 1 mm weld before GltfToScene scale
    };

    public sealed record OptimizeStats(long BeforeTriangles, long AfterTriangles, int Meshes);

    public static OptimizeStats OptimizeSubtree(SceneNode root, MeshCleanupOptions? options = null)
    {
        var opts   = options ?? DefaultStandOptions;
        long before = 0, after = 0;
        int meshes  = 0;

        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } mesh) continue;
            meshes++;
            before += SceneTriangleStats.TriangleCount(mesh);
            var result = MeshCleanup.Clean(mesh, opts);
            n.PendingMesh = result.Mesh;
            after += SceneTriangleStats.TriangleCount(result.Mesh);
        }

        return new OptimizeStats(before, after, meshes);
    }
}