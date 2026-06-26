using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>Writes triangle meshes as binary STL (Z-up millimetres).</summary>
public static class StlExporter
{
    public static void Write(string path, MeshData mesh)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var triangles = EnumerateTriangles(mesh).ToList();
        using var fs = File.Create(path);
        using var w  = new BinaryWriter(fs);

        w.Write(new byte[80]);
        w.Write((uint)triangles.Count);

        foreach (var (a, b, c, normal) in triangles)
        {
            WriteVec3(w, normal);
            WriteVec3(w, a);
            WriteVec3(w, b);
            WriteVec3(w, c);
            w.Write((ushort)0);
        }
    }

    private static IEnumerable<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)> EnumerateTriangles(MeshData mesh)
    {
        if (mesh.Indices is { Length: > 0 } idx)
        {
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                var a = mesh.Positions[idx[i]];
                var b = mesh.Positions[idx[i + 1]];
                var c = mesh.Positions[idx[i + 2]];
                yield return (a, b, c, ComputeNormal(a, b, c));
            }
            yield break;
        }

        for (int i = 0; i + 2 < mesh.Positions.Length; i += 3)
        {
            var a = mesh.Positions[i];
            var b = mesh.Positions[i + 1];
            var c = mesh.Positions[i + 2];
            var n = i + 2 < mesh.Normals.Length ? mesh.Normals[i] : ComputeNormal(a, b, c);
            yield return (a, b, c, n);
        }
    }

    private static Vector3 ComputeNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        return n.LengthSquared > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
    }

    private static void WriteVec3(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }
}