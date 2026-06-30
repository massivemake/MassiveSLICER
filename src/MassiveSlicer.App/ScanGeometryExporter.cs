using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App;

/// <summary>Extracts scan geometry in world space and writes PLY / STL exports.</summary>
internal static class ScanGeometryExporter
{
    public static bool TryExtractWorldMesh(SceneNode node, out MeshData mesh)
    {
        mesh = null!;
        if (!TryGetSourceMesh(node, out var source))
            return false;

        mesh = TransformMeshToWorld(source, node.WorldTransform);
        return true;
    }

    public static bool HasTriangleMesh(SceneNode node)
        => TryGetSourceMesh(node, out var mesh) && mesh.Indices is { Length: >= 3 };

    public static void ExportPointCloud(string path, SceneNode node)
    {
        if (!TryExtractWorldMesh(node, out var world))
            throw new InvalidOperationException("Scan has no exportable geometry.");

        var cloud = new MeshData(
            world.Positions,
            world.Normals,
            indices: null,
            world.Name,
            world.BaseColor,
            world.Metallic,
            world.Roughness)
        {
            RenderAsPoints = true,
        };

        PlyExporter.Write(path, cloud);
    }

    public static void ExportMesh(string path, SceneNode node)
    {
        if (!TryExtractWorldMesh(node, out var world))
            throw new InvalidOperationException("Scan has no exportable geometry.");

        if (world.Indices is not { Length: >= 3 })
            throw new InvalidOperationException("Scan has no triangle mesh to export.");

        StlExporter.Write(path, world);
    }

    static bool TryGetSourceMesh(SceneNode node, out MeshData mesh)
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

    static MeshData TransformMeshToWorld(MeshData local, Matrix4 world)
    {
        var positions = new Vector3[local.Positions.Length];
        var normals   = new Vector3[local.Normals.Length];
        for (int i = 0; i < positions.Length; i++)
            positions[i] = TransformPoint(local.Positions[i], world);
        for (int i = 0; i < normals.Length; i++)
            normals[i] = TransformNormal(local.Normals[i], world);

        return new MeshData(
            positions,
            normals,
            local.Indices,
            local.Name,
            local.BaseColor,
            local.Metallic,
            local.Roughness)
        {
            RenderAsPoints = local.RenderAsPoints,
        };
    }

    static Vector3 TransformPoint(Vector3 p, Matrix4 m)
        => new(
            p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42,
            p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);

    static Vector3 TransformNormal(Vector3 n, Matrix4 m)
    {
        var x = n.X * m.M11 + n.Y * m.M21 + n.Z * m.M31;
        var y = n.X * m.M12 + n.Y * m.M22 + n.Z * m.M32;
        var z = n.X * m.M13 + n.Y * m.M23 + n.Z * m.M33;
        var len = MathF.Sqrt(x * x + y * y + z * z);
        return len > 1e-6f ? new Vector3(x / len, y / len, z / len) : n;
    }
}