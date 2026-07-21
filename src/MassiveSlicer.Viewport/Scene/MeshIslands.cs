using System.Linq;
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
    /// <summary>
    /// Weld distance loose enough to bridge a cap face to its own surrounding wall —
    /// <see cref="PlanarMeshSplitter.CapLoop"/> nudges every cap vertex by <c>1e-3</c> off the
    /// true boundary purely to avoid z-fighting, so without this, every ordinary cut's cap would
    /// register as a phantom extra island on top of the real piece it belongs to.
    /// </summary>
    public const float LooseWeldEps = 1.5e-2f;

    /// <summary>
    /// Weld distance for judging whether two DIFFERENT, already-resolved pieces (e.g. a bounded
    /// cut's Positive side vs its Negative side, or either vs the untouched material outside the
    /// cut's footprint) are actually still connected. Deliberately tight: Positive's and
    /// Negative's wall boundaries sit on the exact same curve by construction (that's what makes
    /// them a matched cut), so a cap vertex sitting <see cref="LooseWeldEps"/>-ish off that curve
    /// is equidistant from "its own wall" and "the opposite wall" — no epsilon or bias direction
    /// can tell those two cases apart by distance alone. This has to stay far tighter than that
    /// ambiguity band, relying instead on genuinely exact (or near-exact, from float
    /// interpolation) shared positions at real, non-doubled boundaries — e.g. between the
    /// untouched "outside the footprint" material and the wall it was clipped from, which was
    /// never capped or biased at all.
    /// </summary>
    public const float StrictWeldEps = 1e-4f;

    /// <summary>Returns <paramref name="mesh"/> unchanged (as a single-element list) if it's
    /// already one connected piece, or one <see cref="MeshData"/> per disconnected island
    /// otherwise. Connectivity is by shared vertex position — the splitter's output is an
    /// unwelded flat triangle soup (every triangle owns 3 fresh vertex copies), so adjacency can't
    /// be read off the index buffer and has to be found by position instead.</summary>
    public static List<MeshData> Split(MeshData mesh, float weldEps = LooseWeldEps)
    {
        var pos = mesh.Positions;
        var nrm = mesh.Normals;
        var idx = mesh.Indices;
        int triCount = idx is { Length: > 0 } ? idx.Length / 3 : pos.Length / 3;
        if (triCount <= 1) return [mesh];

        int VertAt(int t, int corner) => idx is { } ind ? (int)ind[t * 3 + corner] : t * 3 + corner;

        var parent = new int[pos.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

        // Every triangle's own 3 corners are trivially one connected unit.
        for (int t = 0; t < triCount; t++)
        {
            int a = VertAt(t, 0), b = VertAt(t, 1), c = VertAt(t, 2);
            Union(a, b); Union(b, c);
        }

        WeldByPosition(pos, weldEps, Union);

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

    /// <summary>Fragment group, for the one pairing <see cref="MergeFragments"/> must never treat
    /// as a real connection.</summary>
    public enum FragmentSide { Positive, Negative, Outside }

    /// <summary>
    /// Merges a list of already-independently-resolved fragments (one bounded cut's Positive
    /// islands + Negative islands + untouched-outside-the-footprint fragments) into their final
    /// connected groups, using <see cref="StrictWeldEps"/>. Positive and Negative are NEVER
    /// unioned directly with each other, no matter how close their vertices sit: a cut's two sides
    /// share their boundary curve EXACTLY by construction (that's what makes them a matched pair,
    /// not two separately-touching solids), so any distance test — no matter how strict — would
    /// always "detect" that as contact. That contact is not a real bridge; it's just what a clean
    /// cut looks like. The only thing that can legitimately reconnect Positive and Negative is
    /// Outside material bridging between them (material that was never actually cut, and so has
    /// no such guaranteed coincidence with anything).
    /// </summary>
    public static List<MeshData> MergeFragments(
        IReadOnlyList<(MeshData Mesh, FragmentSide Side)> fragments, float weldEps = StrictWeldEps)
    {
        if (fragments.Count <= 1) return [.. fragments.Select(f => f.Mesh)];

        var parent = new int[fragments.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

        // Tag every vertex position with which fragment it came from, then weld across fragments
        // (skip same-fragment pairs — that connectivity is already resolved; skip Positive<->
        // Negative pairs entirely — see summary).
        var allPos = new List<Vector3>();
        var owner  = new List<int>();
        for (int f = 0; f < fragments.Count; f++)
            foreach (var p in fragments[f].Mesh.Positions)
            {
                allPos.Add(p);
                owner.Add(f);
            }

        WeldByPosition(allPos, weldEps, (i, j) =>
        {
            var (sa, sb) = (fragments[owner[i]].Side, fragments[owner[j]].Side);
            bool isPositiveNegativePair =
                (sa == FragmentSide.Positive && sb == FragmentSide.Negative) ||
                (sa == FragmentSide.Negative && sb == FragmentSide.Positive);
            if (!isPositiveNegativePair) Union(owner[i], owner[j]);
        });

        var groups = new Dictionary<int, List<MeshData>>();
        for (int f = 0; f < fragments.Count; f++)
        {
            int root = Find(f);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = [];
            list.Add(fragments[f].Mesh);
        }

        return [.. groups.Values.Select(g => g.Count == 1 ? g[0] : MeshConcat.Concat(g))];
    }

    /// <summary>
    /// Unions vertex indices whose positions are within <paramref name="weldEps"/> of each other.
    /// Deliberately NOT a naive "round each coordinate to a grid cell" quantization: two points
    /// genuinely <paramref name="weldEps"/> apart can straddle a grid boundary and land in
    /// different cells (or, conversely, two points near-but-not-quite that far apart can share a
    /// cell) — grid quantization approximates a distance test but doesn't reliably enforce one.
    /// Instead: bucket by a cell size of <paramref name="weldEps"/> for broad-phase, then check
    /// every candidate in the 3x3x3 neighborhood with a real distance test — any pair actually
    /// within range is guaranteed to share a cell or land in an immediately adjacent one.
    /// </summary>
    private static void WeldByPosition(IReadOnlyList<Vector3> pos, float weldEps, Action<int, int> union)
    {
        int Cell(float v) => (int)MathF.Floor(v / weldEps);
        var buckets = new Dictionary<(int, int, int), List<int>>(pos.Count);
        for (int i = 0; i < pos.Count; i++)
        {
            var p = pos[i];
            var (cx, cy, cz) = (Cell(p.X), Cell(p.Y), Cell(p.Z));
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!buckets.TryGetValue((cx + dx, cy + dy, cz + dz), out var candidates)) continue;
                foreach (var j in candidates)
                    if ((pos[j] - p).LengthSquared <= weldEps * weldEps)
                        union(i, j);
            }
            var key = (cx, cy, cz);
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
            list.Add(i);
        }
    }
}
