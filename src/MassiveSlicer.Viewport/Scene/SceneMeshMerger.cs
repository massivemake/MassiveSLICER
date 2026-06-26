using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Combines many small CAD shell meshes into one draw call while preserving the root transform.
/// </summary>
public static class SceneMeshMerger
{
    public sealed record MergeStats(int SourceMeshes, long Triangles);

    /// <summary>
    /// Replaces <paramref name="root"/>'s subtree with a single child that holds one merged
    /// <see cref="MeshData"/>. The root's <see cref="SceneNode.LocalTransform"/> is unchanged.
    /// </summary>
    public static MergeStats MergeSubtree(SceneNode root, string mergedMeshName)
    {
        var positions = new List<Vector3>();
        var normals   = new List<Vector3>();
        var indices   = new List<uint>();
        int sourceMeshes = 0;
        Vector4? color = null;
        float metallic = 0.25f, roughness = 0.6f;

        foreach (var n in root.SelfAndDescendants())
        {
            if (ReferenceEquals(n, root) || n.PendingMesh is not { } mesh) continue;
            sourceMeshes++;

            color ??= mesh.BaseColor;
            var toRoot = TransformRelativeToRoot(n, root);

            uint baseIndex = (uint)positions.Count;
            foreach (var p in mesh.Positions)
                positions.Add(TransformPoint(p, toRoot));
            foreach (var nm in mesh.Normals)
                normals.Add(TransformNormal(nm, toRoot));

            if (mesh.Indices is { Length: > 0 } idx)
            {
                foreach (var i in idx)
                    indices.Add(baseIndex + i);
            }
            else
            {
                for (uint i = 0; i < mesh.Positions.Length; i++)
                    indices.Add(baseIndex + i);
            }
        }

        foreach (var child in root.Children.ToList())
            root.RemoveChild(child);

        if (positions.Count == 0)
            return new MergeStats(0, 0);

        var merged = new MeshData(
            positions.ToArray(), normals.ToArray(), indices.ToArray(),
            mergedMeshName, color, metallic, roughness);

        root.AddChild(new SceneNode
        {
            Name        = mergedMeshName,
            PendingMesh = merged,
            Selectable  = root.Selectable,
        });

        return new MergeStats(sourceMeshes, SceneTriangleStats.TriangleCount(merged));
    }

    private static Matrix4 TransformRelativeToRoot(SceneNode node, SceneNode root)
    {
        var chain = new List<Matrix4>();
        for (var cur = node; cur is not null && !ReferenceEquals(cur, root); cur = cur.Parent)
            chain.Add(cur.LocalTransform);
        chain.Reverse();

        var m = Matrix4.Identity;
        foreach (var t in chain)
            m *= t;
        return m;
    }

    private static Vector3 TransformPoint(Vector3 p, Matrix4 m)
        => new(
            p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42,
            p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);

    private static Vector3 TransformNormal(Vector3 n, Matrix4 m)
    {
        var x = n.X * m.M11 + n.Y * m.M21 + n.Z * m.M31;
        var y = n.X * m.M12 + n.Y * m.M22 + n.Z * m.M32;
        var z = n.X * m.M13 + n.Y * m.M23 + n.Z * m.M33;
        var len = MathF.Sqrt(x * x + y * y + z * z);
        return len > 1e-6f ? new Vector3(x / len, y / len, z / len) : n;
    }
}