namespace MassiveSlicer.Core.Slicing.Curved;

/// <summary>Topological ordering of split mesh parts for consistent layer indexing.</summary>
public static class SplitMeshGraph
{
    public static IReadOnlyList<MeshRegionSplitter.MeshPart> OrderForPrint(
        IReadOnlyList<MeshRegionSplitter.MeshPart> parts)
    {
        if (parts.Count <= 1) return parts;

        // Order by average Z of LOW boundary (bottom-first), then HIGH Z descending tie-break.
        return parts
            .OrderBy(p => AvgZ(p.Mesh, p.LowVertices))
            .ThenByDescending(p => AvgZ(p.Mesh, p.HighVertices))
            .ToList();
    }

    private static float AvgZ(MassiveSlicer.Core.Slicing.WeldedMesh mesh, IReadOnlyList<int> verts)
    {
        if (verts.Count == 0) return 0f;
        float sum = 0f;
        foreach (int v in verts) sum += mesh.Vertices[v].Z;
        return sum / verts.Count;
    }
}