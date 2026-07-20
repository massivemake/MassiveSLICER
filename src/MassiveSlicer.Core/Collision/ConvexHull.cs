using System.Numerics;

namespace MassiveSlicer.Core.Collision;

/// <summary>
/// Immutable convex support set for GJK queries: the hull's vertices and local AABB.
/// Interior points never win a support (max-dot) query, so any superset of the true
/// hull vertices is CORRECT (just slower) — quickhull failure therefore degrades to
/// the decimated input set, never to a wrong answer.
/// </summary>
public sealed class ConvexHull
{
    public Vector3[] Vertices { get; }
    public Aabb LocalBounds { get; }

    public ConvexHull(Vector3[] vertices)
    {
        Vertices = vertices;
        LocalBounds = Aabb.FromPoints(vertices);
    }

    /// <summary>Support point: hull vertex with maximal dot(dir).</summary>
    public Vector3 Support(Vector3 dir)
    {
        var best = Vertices[0];
        float bestDot = Vector3.Dot(best, dir);
        for (int i = 1; i < Vertices.Length; i++)
        {
            float d = Vector3.Dot(Vertices[i], dir);
            if (d > bestDot) { bestDot = d; best = Vertices[i]; }
        }
        return best;
    }
}

/// <summary>3D quickhull + helpers for building collision support sets.</summary>
public static class Quickhull
{
    /// <summary>
    /// Builds a convex hull vertex set. Degenerate input (coplanar/collinear) or any
    /// construction failure falls back to the (deduplicated) input set — safe for GJK.
    /// If the hull exceeds <paramref name="maxVerts"/>, it is voxel-decimated down.
    /// </summary>
    public static ConvexHull Build(IReadOnlyList<Vector3> points, int maxVerts = 256)
    {
        if (points.Count == 0)
            return new ConvexHull([Vector3.Zero]);
        if (points.Count <= 4)
            return new ConvexHull(Dedup(points));

        Vector3[] hullVerts;
        try
        {
            hullVerts = ComputeHullVertices(points) ?? Dedup(points);
        }
        catch
        {
            hullVerts = Dedup(points);
        }

        // Soft vertex cap: grow decimation cell until under the cap.
        if (hullVerts.Length > maxVerts)
        {
            var bounds = Aabb.FromPoints(hullVerts);
            float cell = MathF.Max(bounds.Extent.Length() / 64f, 1e-3f);
            var reduced = hullVerts;
            for (int attempt = 0; attempt < 8 && reduced.Length > maxVerts; attempt++)
            {
                reduced = VoxelDecimate(hullVerts, cell);
                cell *= 1.7f;
            }
            hullVerts = reduced;
        }

        return new ConvexHull(hullVerts);
    }

    /// <summary>
    /// Reduces a point set to at most one representative per voxel cell, always
    /// keeping the 6 axis-extreme points so the support extent never shrinks.
    /// </summary>
    public static Vector3[] VoxelDecimate(IReadOnlyList<Vector3> points, float cellSize)
    {
        if (points.Count == 0) return [];
        cellSize = MathF.Max(cellSize, 1e-6f);

        var seen = new Dictionary<(int, int, int), Vector3>();
        // Axis extremes: (minX, maxX, minY, maxY, minZ, maxZ) holders.
        Span<Vector3> extremes = stackalloc Vector3[6];
        Span<float> extremeVals =
        [
            float.MaxValue, float.MinValue,
            float.MaxValue, float.MinValue,
            float.MaxValue, float.MinValue,
        ];

        foreach (var p in points)
        {
            var key = (
                (int)MathF.Floor(p.X / cellSize),
                (int)MathF.Floor(p.Y / cellSize),
                (int)MathF.Floor(p.Z / cellSize));
            seen.TryAdd(key, p);

            if (p.X < extremeVals[0]) { extremeVals[0] = p.X; extremes[0] = p; }
            if (p.X > extremeVals[1]) { extremeVals[1] = p.X; extremes[1] = p; }
            if (p.Y < extremeVals[2]) { extremeVals[2] = p.Y; extremes[2] = p; }
            if (p.Y > extremeVals[3]) { extremeVals[3] = p.Y; extremes[3] = p; }
            if (p.Z < extremeVals[4]) { extremeVals[4] = p.Z; extremes[4] = p; }
            if (p.Z > extremeVals[5]) { extremeVals[5] = p.Z; extremes[5] = p; }
        }

        var result = new HashSet<Vector3>(seen.Values);
        foreach (var e in extremes) result.Add(e);
        return [.. result];
    }

    static Vector3[] Dedup(IReadOnlyList<Vector3> points)
    {
        var set = new HashSet<Vector3>();
        foreach (var p in points) set.Add(p);
        return [.. set];
    }

    // ── Quickhull proper ─────────────────────────────────────────────────────

    sealed class Face
    {
        public int A, B, C;           // vertex indices, CCW seen from outside
        public Vector3 Normal;        // outward unit normal
        public float D;               // plane offset: dot(Normal, v) = D on the plane
        public List<int> Outside = []; // candidate points strictly outside this face
        public bool Alive = true;
    }

    /// <summary>Returns hull vertex positions, or null when input is degenerate.</summary>
    static Vector3[]? ComputeHullVertices(IReadOnlyList<Vector3> pts)
    {
        int n = pts.Count;
        var bounds = Aabb.FromPoints([.. pts]);
        float eps = MathF.Max(bounds.Extent.Length(), 1f) * 1e-6f;

        // 1) Initial simplex: two most distant axis-extreme points…
        Span<int> ext = stackalloc int[6];
        for (int i = 0; i < n; i++)
        {
            var p = pts[i];
            if (p.X < pts[ext[0]].X) ext[0] = i;
            if (p.X > pts[ext[1]].X) ext[1] = i;
            if (p.Y < pts[ext[2]].Y) ext[2] = i;
            if (p.Y > pts[ext[3]].Y) ext[3] = i;
            if (p.Z < pts[ext[4]].Z) ext[4] = i;
            if (p.Z > pts[ext[5]].Z) ext[5] = i;
        }
        int i0 = 0, i1 = 0;
        float best = -1f;
        for (int a = 0; a < 6; a++)
            for (int b = a + 1; b < 6; b++)
            {
                float d = Vector3.DistanceSquared(pts[ext[a]], pts[ext[b]]);
                if (d > best) { best = d; i0 = ext[a]; i1 = ext[b]; }
            }
        if (best < eps * eps) return null; // all points coincident

        // …farthest from that line…
        int i2 = -1; best = eps;
        var dir = Vector3.Normalize(pts[i1] - pts[i0]);
        for (int i = 0; i < n; i++)
        {
            var v = pts[i] - pts[i0];
            float d = (v - dir * Vector3.Dot(v, dir)).Length();
            if (d > best) { best = d; i2 = i; }
        }
        if (i2 < 0) return null; // collinear

        // …farthest from that plane.
        var pn = Vector3.Normalize(Vector3.Cross(pts[i1] - pts[i0], pts[i2] - pts[i0]));
        int i3 = -1; best = eps;
        for (int i = 0; i < n; i++)
        {
            float d = MathF.Abs(Vector3.Dot(pts[i] - pts[i0], pn));
            if (d > best) { best = d; i3 = i; }
        }
        if (i3 < 0) return null; // coplanar

        // 2) Build the initial tetrahedron (faces oriented away from the centroid).
        var centroid = (pts[i0] + pts[i1] + pts[i2] + pts[i3]) * 0.25f;
        var faces = new List<Face>
        {
            MakeFace(pts, i0, i1, i2, centroid),
            MakeFace(pts, i0, i1, i3, centroid),
            MakeFace(pts, i0, i2, i3, centroid),
            MakeFace(pts, i1, i2, i3, centroid),
        };

        // 3) Assign every point to the first face it lies strictly outside of.
        for (int i = 0; i < n; i++)
        {
            if (i == i0 || i == i1 || i == i2 || i == i3) continue;
            foreach (var f in faces)
            {
                if (Vector3.Dot(f.Normal, pts[i]) - f.D > eps)
                {
                    f.Outside.Add(i);
                    break;
                }
            }
        }

        // 4) Iterate: expand toward each face's farthest outside point.
        int guard = 0, guardMax = n * 8 + 64;
        while (guard++ < guardMax)
        {
            Face? work = null;
            foreach (var f in faces)
                if (f.Alive && f.Outside.Count > 0) { work = f; break; }
            if (work is null) break;

            // Farthest outside point of this face.
            int far = -1; float farD = -1f;
            foreach (var pi in work.Outside)
            {
                float d = Vector3.Dot(work.Normal, pts[pi]) - work.D;
                if (d > farD) { farD = d; far = pi; }
            }
            if (far < 0) { work.Outside.Clear(); continue; }
            var fp = pts[far];

            // Visible set + horizon edges (edges shared by exactly one visible face).
            var visible = new List<Face>();
            foreach (var f in faces)
                if (f.Alive && Vector3.Dot(f.Normal, fp) - f.D > eps)
                    visible.Add(f);

            var edgeCount = new Dictionary<(int, int), (int a, int b)>();
            void AddEdge(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                if (!edgeCount.Remove(key))
                    edgeCount[key] = (a, b);
            }
            foreach (var f in visible)
            {
                AddEdge(f.A, f.B);
                AddEdge(f.B, f.C);
                AddEdge(f.C, f.A);
            }

            var orphans = new List<int>();
            foreach (var f in visible)
            {
                f.Alive = false;
                orphans.AddRange(f.Outside);
                f.Outside.Clear();
            }

            // Stitch new faces from horizon edges to the far point.
            var newFaces = new List<Face>();
            foreach (var (a, b) in edgeCount.Values)
            {
                var nf = MakeFace(pts, a, b, far, centroid);
                newFaces.Add(nf);
                faces.Add(nf);
            }

            // Redistribute orphaned points.
            foreach (var pi in orphans)
            {
                if (pi == far) continue;
                foreach (var f in newFaces)
                {
                    if (Vector3.Dot(f.Normal, pts[pi]) - f.D > eps)
                    {
                        f.Outside.Add(pi);
                        break;
                    }
                }
            }

            // Compact dead faces occasionally.
            if (faces.Count > 64 && faces.Count(f => !f.Alive) > faces.Count / 2)
                faces.RemoveAll(f => !f.Alive);
        }
        if (guard >= guardMax) return null; // didn't converge — fall back

        var used = new HashSet<int>();
        foreach (var f in faces)
        {
            if (!f.Alive) continue;
            used.Add(f.A); used.Add(f.B); used.Add(f.C);
        }
        if (used.Count < 4) return null;

        var result = new Vector3[used.Count];
        int k = 0;
        foreach (var i in used) result[k++] = pts[i];
        return result;
    }

    static Face MakeFace(IReadOnlyList<Vector3> pts, int a, int b, int c, Vector3 interior)
    {
        var normal = Vector3.Cross(pts[b] - pts[a], pts[c] - pts[a]);
        float len = normal.Length();
        normal = len > 1e-12f ? normal / len : Vector3.UnitZ;
        float d = Vector3.Dot(normal, pts[a]);
        if (Vector3.Dot(normal, interior) - d > 0f)
        {
            // Flip so the normal points away from the hull interior.
            (b, c) = (c, b);
            normal = -normal;
            d = -d;
        }
        return new Face { A = a, B = b, C = c, Normal = normal, D = d };
    }
}
