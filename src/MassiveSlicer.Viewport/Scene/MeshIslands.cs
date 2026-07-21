using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Splits a mesh into its physically-disconnected pieces ("islands") — triangles connected via
/// shared vertex positions end up together, triangles with no path between them end up as
/// separate <see cref="MeshData"/> results. <see cref="PlanarMeshSplitter"/> only ever buckets
/// triangles by which side of the cut plane they fall on, with no idea whether that bucket is one
/// connected piece or several: a curled/spiral wall can cross the same flat plane at more than one
/// point along its length, so one side of a single cut can legitimately be two separate chunks
/// that would otherwise get silently glued into one mesh/outliner entry.
/// </summary>
public static class MeshIslands
{
    /// <summary>Returns <paramref name="mesh"/> unchanged (as a single-element list) if it's
    /// already one connected piece, or one <see cref="MeshData"/> per disconnected island
    /// otherwise. Connectivity is by shared vertex position (quantized to the same epsilon
    /// <see cref="PlanarMeshSplitter"/> uses for its own on-plane test) — the splitter's output is
    /// an unwelded flat triangle soup (every triangle owns 3 fresh vertex copies), so adjacency
    /// can't be read off the index buffer and has to be found by position instead.</summary>
    public static List<MeshData> Split(MeshData mesh)
    {
        var pos = mesh.Positions;
        var nrm = mesh.Normals;
        var idx = mesh.Indices;
        int triCount = idx is { Length: > 0 } ? idx.Length / 3 : pos.Length / 3;
        if (triCount <= 1) return [mesh];

        int VertAt(int t, int corner) => idx is { } ind ? (int)ind[t * 3 + corner] : t * 3 + corner;

        var parent = new int[pos.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a != b) parent[a] = b;
        }

        // Every triangle's own 3 corners are trivially one connected unit.
        for (int t = 0; t < triCount; t++)
        {
            int a = VertAt(t, 0), b = VertAt(t, 1), c = VertAt(t, 2);
            Union(a, b); Union(b, c);
        }

        // Weld separate triangles together wherever they share a vertex position — this is what
        // actually links triangle to triangle, since the index buffer never does (see summary).
        const float weldEps = 1e-4f;
        long Q(float v) => (long)MathF.Round(v / weldEps);
        var byPos = new Dictionary<(long, long, long), int>(pos.Length);
        for (int i = 0; i < pos.Length; i++)
        {
            var key = (Q(pos[i].X), Q(pos[i].Y), Q(pos[i].Z));
            if (byPos.TryGetValue(key, out var first)) Union(first, i);
            else byPos[key] = i;
        }

        var groups = new Dictionary<int, List<int>>();
        for (int t = 0; t < triCount; t++)
        {
            int root = Find(VertAt(t, 0));
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = [];
            list.Add(t);
        }

        if (groups.Count <= 1) return [mesh];

        var result = new List<MeshData>(groups.Count);
        foreach (var tris in groups.Values)
        {
            var outPos = new Vector3[tris.Count * 3];
            var outNrm = new Vector3[tris.Count * 3];
            var outIdx = new uint[tris.Count * 3];
            for (int k = 0; k < tris.Count; k++)
            {
                int t = tris[k];
                int a = VertAt(t, 0), b = VertAt(t, 1), c = VertAt(t, 2);
                outPos[k * 3 + 0] = pos[a]; outPos[k * 3 + 1] = pos[b]; outPos[k * 3 + 2] = pos[c];
                outNrm[k * 3 + 0] = nrm[a]; outNrm[k * 3 + 1] = nrm[b]; outNrm[k * 3 + 2] = nrm[c];
                outIdx[k * 3 + 0] = (uint)(k * 3 + 0);
                outIdx[k * 3 + 1] = (uint)(k * 3 + 1);
                outIdx[k * 3 + 2] = (uint)(k * 3 + 2);
            }
            result.Add(new MeshData(outPos, outNrm, outIdx, mesh.Name, mesh.BaseColor, mesh.Metallic, mesh.Roughness));
        }
        return result;
    }
}
