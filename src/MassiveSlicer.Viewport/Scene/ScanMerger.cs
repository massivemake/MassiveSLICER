using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

public enum ScanMergeOutput
{
    PointCloud,
    Mesh,
}

/// <summary>
/// Merges multiple registered scan meshes into one world-space result with ICP refinement.
/// </summary>
public static class ScanMerger
{
    public sealed record MergeResult(MeshData Mesh, int SourceCount, long PointCount, long TriangleCount);

    /// <summary>
    /// Combines scan nodes using each node's world transform plus pairwise ICP alignment.
    /// Returns <c>null</c> when no geometry is found.
    /// </summary>
    public static MergeResult? Merge(IReadOnlyList<SceneNode> scans, ScanMergeOutput output)
    {
        if (scans.Count == 0) return null;

        var sources = new List<(SceneNode Node, MeshData Mesh, Matrix4 World)>();
        foreach (var node in scans)
        {
            if (!TryGetMesh(node, out var mesh)) continue;
            sources.Add((node, mesh, node.WorldTransform));
        }

        if (sources.Count == 0) return null;

        var alignments = ComputeAlignments(sources);

        var positions = new List<Vector3>();
        var normals   = new List<Vector3>();
        var indices   = new List<uint>();
        var scanColor = new Vector4(0.62f, 0.78f, 0.92f, 1f);

        foreach (var (node, mesh, world) in sources)
        {
            var align = alignments[node];
            var toWorld = world * align;

            uint baseIndex = (uint)positions.Count;
            foreach (var p in mesh.Positions)
                positions.Add(ScanPointCloudAligner.TransformPoint(p, toWorld));
            foreach (var n in mesh.Normals)
                normals.Add(ScanPointCloudAligner.TransformNormal(n, toWorld));

            if (output == ScanMergeOutput.Mesh && mesh.Indices is { Length: > 0 } idx)
            {
                foreach (var i in idx)
                    indices.Add(baseIndex + i);
            }
        }

        if (positions.Count == 0) return null;

        uint[]? outIndices = output == ScanMergeOutput.Mesh && indices.Count > 0
            ? indices.ToArray()
            : null;

        var merged = new MeshData(
            positions.ToArray(),
            normals.ToArray(),
            outIndices,
            "Merged Scan",
            scanColor,
            roughness: 0.9f)
        {
            RenderAsPoints = output == ScanMergeOutput.PointCloud,
        };

        long tris = outIndices is { Length: > 0 } ix ? ix.Length / 3 : 0;
        return new MergeResult(merged, sources.Count, positions.Count, tris);
    }

    /// <summary>Re-expresses world-space vertices in a scan-local frame (row-vector convention).</summary>
    public static MeshData ToLocalFrame(MeshData worldMesh, Matrix4 worldFromLocal)
    {
        var toLocal = worldFromLocal.Inverted();
        var positions = new Vector3[worldMesh.Positions.Length];
        var normals   = new Vector3[worldMesh.Normals.Length];
        for (int i = 0; i < positions.Length; i++)
            positions[i] = ScanPointCloudAligner.TransformPoint(worldMesh.Positions[i], toLocal);
        for (int i = 0; i < normals.Length; i++)
            normals[i] = ScanPointCloudAligner.TransformNormal(worldMesh.Normals[i], toLocal);

        return new MeshData(
            positions,
            normals,
            worldMesh.Indices,
            worldMesh.Name,
            worldMesh.BaseColor,
            worldMesh.Metallic,
            worldMesh.Roughness)
        {
            RenderAsPoints = worldMesh.RenderAsPoints,
        };
    }

    private static Dictionary<SceneNode, Matrix4> ComputeAlignments(
        List<(SceneNode Node, MeshData Mesh, Matrix4 World)> sources)
    {
        var alignments = new Dictionary<SceneNode, Matrix4>();
        if (sources.Count == 0) return alignments;

        alignments[sources[0].Node] = Matrix4.Identity;
        var reference = ExtractWorldPoints(sources[0].Mesh, sources[0].World);

        for (int i = 1; i < sources.Count; i++)
        {
            var (node, mesh, world) = sources[i];
            var moving = ExtractWorldPoints(mesh, world);
            var step   = ScanPointCloudAligner.AlignToReferenceRotationOnly(reference, moving);
            alignments[node] = step;

            // Grow the reference set so later scans align to the accumulating model.
            foreach (var p in moving)
                reference.Add(ScanPointCloudAligner.TransformPoint(p, step));
        }

        return alignments;
    }

    private static List<Vector3> ExtractWorldPoints(MeshData mesh, Matrix4 world)
    {
        var pts = new List<Vector3>(mesh.Positions.Length);
        foreach (var p in mesh.Positions)
            pts.Add(ScanPointCloudAligner.TransformPoint(p, world));
        return pts;
    }

    private static bool TryGetMesh(SceneNode node, out MeshData mesh)
    {
        foreach (var n in node.SelfAndDescendants())
        {
            if (n.Mesh?.PickingData is { } gpu)
            {
                mesh = gpu;
                return true;
            }

            if (n.PendingMesh is { } pending)
            {
                mesh = pending;
                return true;
            }
        }

        mesh = null!;
        return false;
    }
}