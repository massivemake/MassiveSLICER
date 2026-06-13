using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Triangulates an organized (grid-ordered) point cloud into a renderable mesh.
/// Adjacent grid cells become triangle pairs; cells containing invalid (NaN)
/// points or edges longer than the bridge threshold are skipped so depth
/// discontinuities don't web over.
/// </summary>
public static class PointCloudMesher
{
    /// <summary>
    /// Builds a mesh node from row-major XYZ triples (<paramref name="width"/> ×
    /// <paramref name="height"/> grid, NaN = invalid). Triangle winding assumes a
    /// camera looking down +Z, producing normals that face back toward the camera.
    /// Returns <c>null</c> when the cloud has too few valid points to mesh.
    /// </summary>
    public static SceneNode? Build(float[] pointsXYZ, int width, int height,
                                   string name, float maxEdgeMm = 10f)
    {
        int gridCount = width * height;

        // Compact valid points; map[gridIndex] → vertex index or -1.
        var map       = new int[gridCount];
        var positions = new List<Vector3>(gridCount / 2);
        for (int i = 0; i < gridCount; i++)
        {
            float x = pointsXYZ[i * 3];
            if (float.IsNaN(x))
            {
                map[i] = -1;
                continue;
            }
            map[i] = positions.Count;
            positions.Add(new Vector3(x, pointsXYZ[i * 3 + 1], pointsXYZ[i * 3 + 2]));
        }

        if (positions.Count < 3) return null;

        var verts     = positions.ToArray();
        var indices   = new List<uint>(gridCount * 3);
        var normals   = new Vector3[verts.Length];
        float maxEdgeSq = maxEdgeMm * maxEdgeMm;

        for (int r = 0; r < height - 1; r++)
        {
            int row = r * width;
            for (int c = 0; c < width - 1; c++)
            {
                int v00 = map[row + c];
                int v10 = map[row + c + 1];
                int v01 = map[row + width + c];
                int v11 = map[row + width + c + 1];

                // Winding (v00, v01, v10) order makes normals face the camera (-Z).
                if (v00 >= 0 && v01 >= 0 && v11 >= 0)
                    TryAddTriangle(verts, indices, normals, v00, v01, v11, maxEdgeSq);
                if (v00 >= 0 && v11 >= 0 && v10 >= 0)
                    TryAddTriangle(verts, indices, normals, v00, v11, v10, maxEdgeSq);
            }
        }

        if (indices.Count == 0) return null;

        for (int i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared > 1e-12f
                ? normals[i].Normalized()
                : -Vector3.UnitZ;

        var mesh = new MeshData(verts, normals, indices.ToArray(), name,
                                baseColor: new Vector4(0.62f, 0.78f, 0.92f, 1f),
                                roughness: 0.9f);
        return new SceneNode { Name = name, PendingMesh = mesh };
    }

    private static void TryAddTriangle(Vector3[] verts, List<uint> indices,
                                       Vector3[] normals, int a, int b, int c,
                                       float maxEdgeSq)
    {
        var pa = verts[a];
        var pb = verts[b];
        var pc = verts[c];

        if ((pb - pa).LengthSquared > maxEdgeSq ||
            (pc - pb).LengthSquared > maxEdgeSq ||
            (pa - pc).LengthSquared > maxEdgeSq)
            return;

        indices.Add((uint)a);
        indices.Add((uint)b);
        indices.Add((uint)c);

        // Area-weighted face normal accumulated per vertex.
        var n = Vector3.Cross(pb - pa, pc - pa);
        normals[a] += n;
        normals[b] += n;
        normals[c] += n;
    }
}
