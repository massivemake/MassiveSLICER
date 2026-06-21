namespace MassiveSlicer.Viewport.Scene;

public static class SceneTriangleStats
{
    public static (int Meshes, long Triangles) Count(SceneNode? root)
    {
        if (root is null) return (0, 0);

        int meshes = 0;
        long tris  = 0;
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is { } pending)
            {
                meshes++;
                tris += TriangleCount(pending);
                continue;
            }

            if (n.Mesh?.PickingData is { } gpu)
            {
                meshes++;
                tris += TriangleCount(gpu);
            }
        }

        return (meshes, tris);
    }

    public static long TriangleCount(MeshData mesh)
        => mesh.Indices is { Length: > 0 } idx ? idx.Length / 3 : mesh.Positions.Length / 3;
}