using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Display-only thinning for a scan. Drawn vertices are copied from the source.
/// A mesh or cloud that is denser than <see cref="TargetSamples"/> becomes a solid
/// surface: each grid cell keeps the measured points at its edges and fills the
/// cell with triangles. The original micro-triangles are not drawn on their own,
/// because at this spacing they read as specks. Nothing is welded, averaged, or
/// projected, and the source arrays are not written.
/// </summary>
internal static class ScanDisplaySubset
{
    /// <summary>
    /// About 180k samples. Cell size comes from the two longest sides, so a
    /// two-metre scan stays a few millimetres per cell. A scan already under
    /// this cap is drawn whole.
    /// </summary>
    public const int TargetSamples = 180_000;

    /// <summary>
    /// Builds a smaller position and normal array for the graphics card.
    /// Returns false when the source should be drawn as it is. On success the
    /// outputs are non-indexed triangles whose corners are source vertices.
    /// </summary>
    public static bool TryBuild(
        MeshData source,
        out Vector3[] positions,
        out Vector3[] normals,
        int target = TargetSamples)
        => TryBuild(source, out positions, out normals, out _, target);

    /// <summary>
    /// Same thinning as <see cref="TryBuild(MeshData, out Vector3[], out Vector3[], int)"/>.
    /// <paramref name="sourceIndices"/> is the measured vertex each drawn corner was copied from.
    /// </summary>
    public static bool TryBuild(
        MeshData source,
        out Vector3[] positions,
        out Vector3[] normals,
        out int[] sourceIndices,
        int target = TargetSamples)
    {
        positions = source.Positions;
        normals = source.Normals;
        sourceIndices = [];
        if (!source.ReduceScanDisplay) return false;
        if (source.Uvs is not null || source.Tangents is not null || source.Material is not null)
            return false;
        if (target < 1) return false;
        if (source.Normals.Length != source.Positions.Length) return false;

        int samples = source.RenderAsPoints
            ? source.Positions.Length
            : source.Indices is { Length: >= 3 } idx
                ? idx.Length / 3
                : source.Positions.Length / 3;
        if (samples <= target) return false;

        var (min, max) = source.LocalBounds;
        float cell = CellSize(max - min, target);
        if (cell <= 0f) return false;

        int drop = ShortestAxis(max - min);
        return BuildSolid(source, cell, min, drop, samples, out positions, out normals, out sourceIndices);
    }

    /// <summary>0 = X, 1 = Y, 2 = Z. The thin side of the scan is the one not drawn as the grid.</summary>
    static int ShortestAxis(Vector3 extent)
    {
        float x = MathF.Abs(extent.X), y = MathF.Abs(extent.Y), z = MathF.Abs(extent.Z);
        if (y <= x && y <= z) return 1;
        if (z <= x && z <= y) return 2;
        return 0;
    }

    static int UAxis(int drop) => drop == 0 ? 1 : 0;

    static int VAxis(int drop) => drop == 2 ? 1 : 2;

    static float AxisValue(Vector3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

    static bool BuildSolid(
        MeshData source,
        float cell,
        Vector3 min,
        int drop,
        int samples,
        out Vector3[] positions,
        out Vector3[] normals,
        out int[] sourceIndices)
    {
        var srcP = source.Positions;
        var srcN = source.Normals;
        int uAxis = UAxis(drop);
        int vAxis = VAxis(drop);
        float originU = AxisValue(min, uAxis);
        float originV = AxisValue(min, vAxis);
        var cells = new Dictionary<long, Pick>(Math.Min(TargetSamples, Math.Max(samples, 1)));
        int gMinX = 0, gMaxX = 0, gMinY = 0, gMaxY = 0, gMinZ = 0, gMaxZ = 0;
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        void Consider(int index)
        {
            var p = srcP[index];
            if (p.X < minX) { minX = p.X; gMinX = index; }
            if (p.X > maxX) { maxX = p.X; gMaxX = index; }
            if (p.Y < minY) { minY = p.Y; gMinY = index; }
            if (p.Y > maxY) { maxY = p.Y; gMaxY = index; }
            if (p.Z < minZ) { minZ = p.Z; gMinZ = index; }
            if (p.Z > maxZ) { maxZ = p.Z; gMaxZ = index; }

            int iu = CellIndex(AxisValue(p, uAxis), originU, cell);
            int iv = CellIndex(AxisValue(p, vAxis), originV, cell);
            float cu = originU + (iu + 0.5f) * cell;
            float cv = originV + (iv + 0.5f) * cell;
            float du = AxisValue(p, uAxis) - cu;
            float dv = AxisValue(p, vAxis) - cv;
            float dist2 = du * du + dv * dv;
            long key = ((long)iu << 32) | (uint)iv;
            if (!cells.TryGetValue(key, out var pick) || dist2 < pick.Dist2)
                cells[key] = new Pick(index, dist2, iu, iv);
        }

        if (source.RenderAsPoints)
        {
            for (int i = 0; i < srcP.Length; i++) Consider(i);
        }
        else
        {
            var indices = source.Indices;
            for (int tri = 0; tri < samples; tri++)
            {
                if (!Corners(srcP, indices, tri, out int i0, out int i1, out int i2)) continue;
                Consider(i0);
                Consider(i1);
                Consider(i2);
            }
        }

        var tris = new List<int>(cells.Count * 6);
        var used = new HashSet<int>();
        var anchors = new List<int>();
        foreach (var pick in cells.Values)
            EmitPatch(cells, pick.U, pick.V, srcP, srcN, tris, used, anchors);

        ForceExtreme(srcP, gMinX, tris, used, anchors);
        ForceExtreme(srcP, gMaxX, tris, used, anchors);
        ForceExtreme(srcP, gMinY, tris, used, anchors);
        ForceExtreme(srcP, gMaxY, tris, used, anchors);
        ForceExtreme(srcP, gMinZ, tris, used, anchors);
        ForceExtreme(srcP, gMaxZ, tris, used, anchors);

        int sourceCorners = source.RenderAsPoints ? srcP.Length : samples * 3;
        if (tris.Count < 3 || tris.Count >= sourceCorners)
        {
            positions = srcP;
            normals = srcN;
            sourceIndices = [];
            return false;
        }

        positions = new Vector3[tris.Count];
        normals = new Vector3[tris.Count];
        sourceIndices = new int[tris.Count];
        for (int i = 0; i < tris.Count; i++)
        {
            int s = tris[i];
            positions[i] = srcP[s];
            normals[i] = srcN[s];
            sourceIndices[i] = s;
        }
        return true;
    }

    static void EmitPatch(
        Dictionary<long, Pick> cells,
        int u,
        int v,
        Vector3[] srcP,
        Vector3[] srcN,
        List<int> tris,
        HashSet<int> used,
        List<int> anchors)
    {
        bool a = cells.TryGetValue(Key(u, v), out var pa);
        bool b = cells.TryGetValue(Key(u + 1, v), out var pb);
        bool c = cells.TryGetValue(Key(u, v + 1), out var pc);
        bool d = cells.TryGetValue(Key(u + 1, v + 1), out var pd);
        if (a && b && d) AddFacing(srcP, srcN, pa.Index, pb.Index, pd.Index, tris, used, anchors);
        if (a && d && c) AddFacing(srcP, srcN, pa.Index, pd.Index, pc.Index, tris, used, anchors);
        if (!(a && b && d) && a && b && c)
            AddFacing(srcP, srcN, pa.Index, pb.Index, pc.Index, tris, used, anchors);
        else if (!(a && d && c) && b && c && d)
            AddFacing(srcP, srcN, pb.Index, pd.Index, pc.Index, tris, used, anchors);
    }

    static void AddFacing(
        Vector3[] srcP, Vector3[] srcN, int a, int b, int c,
        List<int> tris, HashSet<int> used, List<int> anchors)
    {
        var normal = srcN[a] + srcN[b] + srcN[c];
        var face = Vector3.Cross(srcP[b] - srcP[a], srcP[c] - srcP[a]);
        if (Vector3.Dot(face, normal) < 0f)
            (b, c) = (c, b);
        AddTriangle(srcP, a, b, c, tris, used, anchors);
    }

    static long Key(int u, int v) => ((long)u << 32) | (uint)v;

    static void ForceExtreme(Vector3[] srcP, int index, List<int> tris, HashSet<int> used, List<int> anchors)
    {
        if (index < 0 || used.Contains(index) || anchors.Count < 2) return;
        var p = srcP[index];
        int n0 = -1, n1 = -1;
        float d0 = float.MaxValue, d1 = float.MaxValue;
        foreach (int candidate in anchors)
        {
            float d = (srcP[candidate] - p).LengthSquared;
            if (d < d0)
            {
                d1 = d0;
                n1 = n0;
                d0 = d;
                n0 = candidate;
            }
            else if (d < d1)
            {
                d1 = d;
                n1 = candidate;
            }
        }
        if (n0 >= 0 && n1 >= 0)
            AddTriangle(srcP, index, n0, n1, tris, used, anchors);
    }

    static void AddTriangle(Vector3[] srcP, int a, int b, int c, List<int> tris, HashSet<int> used, List<int> anchors)
    {
        if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c) return;
        var cross = Vector3.Cross(srcP[b] - srcP[a], srcP[c] - srcP[a]);
        if (cross.LengthSquared < 1e-8f) return;
        tris.Add(a);
        tris.Add(b);
        tris.Add(c);
        Remember(used, anchors, a);
        Remember(used, anchors, b);
        Remember(used, anchors, c);
    }

    static void Remember(HashSet<int> used, List<int> anchors, int index)
    {
        if (used.Add(index)) anchors.Add(index);
    }

    /// <summary>
    /// Cell from the two longest sides, and from the volume when that cell is larger.
    /// A flat sheet stays dense. A solid cloud does not allocate a cell per surface sample.
    /// A line uses its length, because the other two sides are missing.
    /// </summary>
    static float CellSize(Vector3 extent, int target)
    {
        float x = MathF.Max(0f, extent.X);
        float y = MathF.Max(0f, extent.Y);
        float z = MathF.Max(0f, extent.Z);
        float a = x, b = y, c = z;
        if (b > a) (a, b) = (b, a);
        if (c > b) (b, c) = (c, b);
        if (b > a) (a, b) = (b, a);
        if (a < 1e-3f) return 0f;

        float bUse = b < 1e-2f ? a : b;
        float cUse = MathF.Max(c, 1e-2f);
        float surface = MathF.Sqrt((a * bUse) / target);
        float volume = MathF.Cbrt((a * bUse * cUse) / target);
        float size = MathF.Max(surface, volume);
        return size < 1e-3f ? 1e-3f : size;
    }

    static int CellIndex(float value, float origin, float cell)
    {
        int i = (int)MathF.Floor((value - origin) / cell);
        if (i < 0) return 0;
        return i > 0x1FFFFF ? 0x1FFFFF : i;
    }

    static bool Corners(Vector3[] positions, uint[]? indices, int tri, out int i0, out int i1, out int i2)
    {
        if (indices is not null)
        {
            int o = tri * 3;
            i0 = (int)indices[o];
            i1 = (int)indices[o + 1];
            i2 = (int)indices[o + 2];
        }
        else
        {
            i0 = tri * 3;
            i1 = i0 + 1;
            i2 = i0 + 2;
        }
        int n = positions.Length;
        return (uint)i0 < (uint)n && (uint)i1 < (uint)n && (uint)i2 < (uint)n;
    }

    readonly struct Pick
    {
        public readonly int Index;
        public readonly float Dist2;
        public readonly int U;
        public readonly int V;
        public Pick(int index, float dist2, int u, int v)
        {
            Index = index;
            Dist2 = dist2;
            U = u;
            V = v;
        }
    }
}
