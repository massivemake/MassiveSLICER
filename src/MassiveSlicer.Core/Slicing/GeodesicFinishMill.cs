using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Multi-axis finishing as geodesic surface offsets. Distance is measured along
/// the mesh from its boundary, or from the bottom rim when the mesh is closed.
/// Each offset is one contour. The tool axis is the surface normal.
/// </summary>
public static class GeodesicFinishMill
{
    const int MaxContours = 256;

    public static Toolpath Generate(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<int> indices,
        MillSettings mill)
    {
        var tp = new Toolpath();
        if (positions.Count == 0 || indices.Count < 3) return tp;

        var (mesh, vertN) = Weld(positions, normals, indices);
        if (mesh.VertexCount == 0 || mesh.Triangles.Length == 0) return tp;

        var dist = MeshGraph.DijkstraFromSeeds(mesh, Seeds(mesh));
        float maxDist = 0f;
        for (int i = 0; i < dist.Length; i++)
            if (dist[i] < float.MaxValue / 2f && dist[i] > maxDist) maxDist = dist[i];

        float spacing = MathF.Max(0.2f, mill.StepoverMm);
        if (maxDist > spacing * MaxContours)
        {
            spacing = maxDist / MaxContours;
            tp.Warnings.Add($"Multi-axis finishing coarsened the geodesic spacing to {spacing:0.#} mm.");
        }

        float lift = MathF.Max(1f, mill.RapidZMm);
        float leave = mill.FinishAllowanceMm + mill.OffsetDistanceMm;
        var layer = new ToolpathLayer(0, Top(mesh.Vertices)) { PlaneNormal = Vector3.UnitZ };
        Vector3? cursor = null;

        for (float level = spacing * 0.5f; level <= maxDist + 1e-3f; level += spacing)
        {
            var segs = Crossings(mesh, vertN, dist, level);
            foreach (var chain in Chain(segs))
            {
                if (chain.Count < 2) continue;
                var start = Place(chain[0].P, chain[0].N, leave);
                if (cursor is { } prev && Vector3.DistanceSquared(prev, start) > spacing * spacing)
                {
                    var up = prev + chain[0].N * lift;
                    var over = start + chain[0].N * lift;
                    layer.Moves.Add(new ToolpathMove(prev, up, MoveKind.Travel) { Normal = chain[0].N });
                    layer.Moves.Add(new ToolpathMove(up, over, MoveKind.Travel) { Normal = chain[0].N });
                    layer.Moves.Add(new ToolpathMove(over, start, MoveKind.Travel) { Normal = chain[0].N });
                }

                var at = start;
                for (int i = 1; i < chain.Count; i++)
                {
                    var next = Place(chain[i].P, chain[i].N, leave);
                    if (Vector3.DistanceSquared(at, next) < 1e-8f) continue;
                    layer.Moves.Add(new ToolpathMove(at, next, MoveKind.Mill) { Normal = chain[i - 1].N });
                    at = next;
                }
                cursor = at;
            }
        }

        if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        return tp;
    }

    static Vector3 Place(Vector3 p, Vector3 n, float leave)
        => leave == 0f ? p : p + n * leave;

    static float Top(Vector3[] verts)
    {
        float z = float.MinValue;
        foreach (var v in verts)
            if (v.Z > z) z = v.Z;
        return z;
    }

    static (WeldedMesh Mesh, Vector3[] Normals) Weld(
        IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, IReadOnlyList<int> indices)
    {
        var map = new Dictionary<(int, int, int), int>();
        var verts = new List<Vector3>();
        var nsum = new List<Vector3>();
        var tris = new List<int[]>();

        int Id(int index)
        {
            var p = positions[index];
            var key = ((int)MathF.Round(p.X / MeshGraph.SnapGrid),
                       (int)MathF.Round(p.Y / MeshGraph.SnapGrid),
                       (int)MathF.Round(p.Z / MeshGraph.SnapGrid));
            if (!map.TryGetValue(key, out int id))
            {
                id = verts.Count;
                map[key] = id;
                verts.Add(p);
                nsum.Add(Vector3.Zero);
            }
            if (index < normals.Count)
                nsum[id] += normals[index];
            return id;
        }

        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            int a = Id(indices[i]);
            int b = Id(indices[i + 1]);
            int c = Id(indices[i + 2]);
            if (a == b || b == c || a == c) continue;
            tris.Add([a, b, c]);
        }

        var vn = new Vector3[verts.Count];
        for (int i = 0; i < vn.Length; i++)
            vn[i] = nsum[i].LengthSquared() > 1e-8f ? Vector3.Normalize(nsum[i]) : Vector3.UnitZ;

        var va = verts.ToArray();
        return (new WeldedMesh(va, tris.ToArray(), MeshGraph.BuildAdjacency(va, tris.ToArray())), vn);
    }

    static List<int> Seeds(WeldedMesh mesh)
    {
        var uses = new Dictionary<(int, int), int>();
        foreach (var tri in mesh.Triangles)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = tri[k], b = tri[(k + 1) % 3];
                var e = a < b ? (a, b) : (b, a);
                uses.TryGetValue(e, out int n);
                uses[e] = n + 1;
            }
        }

        var seeds = new HashSet<int>();
        foreach (var (e, n) in uses)
        {
            if (n != 1) continue;
            seeds.Add(e.Item1);
            seeds.Add(e.Item2);
        }
        if (seeds.Count > 0) return [.. seeds];

        float zMin = float.MaxValue, zMax = float.MinValue;
        foreach (var v in mesh.Vertices)
        {
            if (v.Z < zMin) zMin = v.Z;
            if (v.Z > zMax) zMax = v.Z;
        }
        float band = MathF.Max(0.5f, (zMax - zMin) * 0.02f);
        var bottom = new List<int>();
        for (int i = 0; i < mesh.VertexCount; i++)
            if (mesh.Vertices[i].Z <= zMin + band) bottom.Add(i);
        return bottom;
    }

    static List<(Vector3 A, Vector3 B, Vector3 N)> Crossings(WeldedMesh mesh, Vector3[] vertN, float[] dist, float level)
    {
        var segs = new List<(Vector3, Vector3, Vector3)>();
        foreach (var tri in mesh.Triangles)
        {
            int i0 = tri[0], i1 = tri[1], i2 = tri[2];
            float d0 = dist[i0], d1 = dist[i1], d2 = dist[i2];
            if (d0 > 1e30f || d1 > 1e30f || d2 > 1e30f) continue;
            float a = d0 - level, b = d1 - level, c = d2 - level;
            var hits = new List<(Vector3 P, Vector3 N)>(2);
            Try(mesh.Vertices[i0], mesh.Vertices[i1], vertN[i0], vertN[i1], a, b, hits);
            Try(mesh.Vertices[i1], mesh.Vertices[i2], vertN[i1], vertN[i2], b, c, hits);
            Try(mesh.Vertices[i2], mesh.Vertices[i0], vertN[i2], vertN[i0], c, a, hits);
            if (hits.Count >= 2)
                segs.Add((hits[0].P, hits[1].P, Normal(hits[0].N, hits[1].N)));
        }
        return segs;
    }

    static void Try(Vector3 p, Vector3 q, Vector3 np, Vector3 nq, float dp, float dq, List<(Vector3 P, Vector3 N)> hits)
    {
        if (dp * dq > 0f || hits.Count >= 2) return;
        float den = dp - dq;
        if (MathF.Abs(den) < 1e-8f) return;
        float t = dp / den;
        if (t < 0f || t > 1f) return;
        var blended = np + (nq - np) * t;
        var n = blended.LengthSquared() > 1e-8f ? Vector3.Normalize(blended) : Vector3.UnitZ;
        hits.Add((p + (q - p) * t, n));
    }

    static Vector3 Normal(Vector3 a, Vector3 b)
    {
        var n = a + b;
        return n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitZ;
    }

    static List<List<(Vector3 P, Vector3 N)>> Chain(List<(Vector3 A, Vector3 B, Vector3 N)> segs)
    {
        var used = new bool[segs.Count];
        var chains = new List<List<(Vector3 P, Vector3 N)>>();
        const float tol = 0.05f;
        for (int i = 0; i < segs.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var chain = new List<(Vector3 P, Vector3 N)> { (segs[i].A, segs[i].N), (segs[i].B, segs[i].N) };
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int j = 0; j < segs.Count; j++)
                {
                    if (used[j]) continue;
                    if (Near(segs[j].A, chain[^1].P, tol)) { chain.Add((segs[j].B, segs[j].N)); used[j] = true; grew = true; break; }
                    if (Near(segs[j].B, chain[^1].P, tol)) { chain.Add((segs[j].A, segs[j].N)); used[j] = true; grew = true; break; }
                    if (Near(segs[j].B, chain[0].P, tol)) { chain.Insert(0, (segs[j].A, segs[j].N)); used[j] = true; grew = true; break; }
                    if (Near(segs[j].A, chain[0].P, tol)) { chain.Insert(0, (segs[j].B, segs[j].N)); used[j] = true; grew = true; break; }
                }
            }
            chains.Add(chain);
        }
        return chains;
    }

    static bool Near(Vector3 a, Vector3 b, float tol) => Vector3.DistanceSquared(a, b) <= tol * tol;
}
