using System.Numerics;
using System.Runtime.CompilerServices;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Horizontal planar slicer. Intersects triangle meshes with Z-planes and chains
/// the resulting segments into ordered contours, then emits extrude + travel moves.
/// </summary>
public static class PlanarSlicer
{
    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Slices all provided meshes and returns a <see cref="Toolpath"/>.
    /// </summary>
    /// <param name="meshes">
    ///   Flat (non-indexed) triangle soups in world space. Each entry is an array
    ///   of positions where every 3 consecutive entries form one triangle.
    /// </param>
    /// <param name="settings">Slice parameters.</param>
    public static Toolpath Slice(
        IReadOnlyList<Vector3[]> meshes,
        SliceSettings settings)
    {
        // ── Compute Z extents across all meshes ───────────────────────────────
        float zMin = float.MaxValue, zMax = float.MinValue;
        foreach (var verts in meshes)
            foreach (var v in verts)
            {
                if (v.Z < zMin) zMin = v.Z;
                if (v.Z > zMax) zMax = v.Z;
            }

        if (zMax <= zMin) return new Toolpath();

        // ── Build Z level list ─────────────────────────────────────────────
        var toolpath = new Toolpath();
        float z    = zMin + settings.FirstLayerHeight;
        int   idx  = 0;
        while (z < zMax - 1e-4f)
        {
            var layer = new ToolpathLayer(idx++, z);
            BuildLayer(meshes, z, settings, layer);
            if (layer.Moves.Count > 0)
                toolpath.Layers.Add(layer);
            z += settings.LayerHeight;
        }

        return toolpath;
    }

    // ── Layer construction ────────────────────────────────────────────────────

    private static void BuildLayer(
        IReadOnlyList<Vector3[]> meshes,
        float z,
        SliceSettings settings,
        ToolpathLayer layer)
    {
        // Collect all intersection segments from all meshes at this Z level.
        var segments = new List<(Vector2 A, Vector2 B)>(64);
        foreach (var verts in meshes)
            CollectSegments(verts, z, segments);

        if (segments.Count == 0) return;

        // Chain segments into contours.
        var contours = ChainSegments(segments);

        // Emit moves: travel to contour start, extrude around contour.
        var lastPos = new Vector2(float.NaN);
        float beadOffset = settings.BeadWidth * 0.5f;

        foreach (var contour in contours)
        {
            if (contour.Count < 2) continue;

            var start = new Vector3(contour[0].X, contour[0].Y, z);

            // Travel move from last position (or first point on first contour).
            if (!float.IsNaN(lastPos.X))
            {
                var travelFrom = new Vector3(lastPos.X, lastPos.Y, z);
                layer.Moves.Add(new ToolpathMove(travelFrom, start, MoveKind.Travel));
            }

            // Extrude along the contour.
            var prev = start;
            for (int i = 1; i < contour.Count; i++)
            {
                var next = new Vector3(contour[i].X, contour[i].Y, z);
                layer.Moves.Add(new ToolpathMove(prev, next, MoveKind.Extrude));
                prev = next;
            }

            // Close the contour only when the chain is topologically closed.
            // Open contours (mesh holes, T-junctions) must not be force-closed —
            // doing so draws a stray extrusion line across the model.
            if (contour.Count > 2)
            {
                float dx = contour[^1].X - contour[0].X;
                float dy = contour[^1].Y - contour[0].Y;
                if (dx * dx + dy * dy < SnapGrid * SnapGrid * 4f)
                    layer.Moves.Add(new ToolpathMove(prev, start, MoveKind.Extrude));
            }

            lastPos = contour[^1];
        }
    }

    // ── Intersection / segment collection ─────────────────────────────────────

    private static void CollectSegments(
        Vector3[] verts,
        float z,
        List<(Vector2, Vector2)> segments)
    {
        Span<Vector2> pts = stackalloc Vector2[2];

        // verts is a flat triangle soup — every 3 entries = one triangle.
        for (int i = 0; i + 2 < verts.Length; i += 3)
        {
            var v0 = verts[i];
            var v1 = verts[i + 1];
            var v2 = verts[i + 2];

            // Signed distances from the cutting plane.
            float d0 = v0.Z - z;
            float d1 = v1.Z - z;
            float d2 = v2.Z - z;

            // Push nearly-on-plane vertices slightly off to avoid degenerate intersections.
            // Preserve sign so a vertex just below the plane stays below.
            if (MathF.Abs(d0) < 1e-5f) d0 = d0 >= 0f ? 1e-5f : -1e-5f;
            if (MathF.Abs(d1) < 1e-5f) d1 = d1 >= 0f ? 1e-5f : -1e-5f;
            if (MathF.Abs(d2) < 1e-5f) d2 = d2 >= 0f ? 1e-5f : -1e-5f;

            // Collect up to 2 edge crossing points.
            int count = 0;
            TryEdge(v0, v1, d0, d1, pts, ref count);
            TryEdge(v1, v2, d1, d2, pts, ref count);
            TryEdge(v2, v0, d2, d0, pts, ref count);

            if (count == 2)
                segments.Add((pts[0], pts[1]));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TryEdge(
        Vector3 a, Vector3 b,
        float da, float db,
        Span<Vector2> pts, ref int count)
    {
        if (count >= 2) return;
        if (da * db >= 0f) return; // same side — no crossing

        float t = da / (da - db);
        pts[count++] = new Vector2(
            a.X + t * (b.X - a.X),
            a.Y + t * (b.Y - a.Y));
    }

    // ── Segment chaining ──────────────────────────────────────────────────────

    // Quantise a coordinate to a grid bucket for endpoint matching.
    private const float SnapGrid = 1e-3f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int, int) Quantise(Vector2 p)
        => ((int)MathF.Round(p.X / SnapGrid),
            (int)MathF.Round(p.Y / SnapGrid));

    private static List<List<Vector2>> ChainSegments(List<(Vector2 A, Vector2 B)> segs)
    {
        // Build adjacency: for each quantised endpoint, store the segment indices that share it.
        var adj = new Dictionary<(int, int), List<int>>();

        void Register(int si, Vector2 pt)
        {
            var key = Quantise(pt);
            if (!adj.TryGetValue(key, out var list))
                adj[key] = list = new List<int>(2);
            list.Add(si);
        }

        for (int i = 0; i < segs.Count; i++)
        {
            Register(i, segs[i].A);
            Register(i, segs[i].B);
        }

        var used     = new bool[segs.Count];
        var contours = new List<List<Vector2>>();

        for (int start = 0; start < segs.Count; start++)
        {
            if (used[start]) continue;

            // Start a new chain.
            var chain = new List<Vector2>();
            used[start] = true;
            chain.Add(segs[start].A);
            chain.Add(segs[start].B);

            // Walk forward from B.
            while (true)
            {
                var tip    = chain[^1];
                var tipKey = Quantise(tip);
                if (!adj.TryGetValue(tipKey, out var candidates)) break;

                int next = -1;
                foreach (int si in candidates)
                {
                    if (used[si]) continue;
                    next = si;
                    break;
                }
                if (next < 0) break;

                used[next] = true;
                // Orient so the segment starts at `tip`.
                var (a, b) = segs[next];
                bool aIsTip = Quantise(a) == tipKey;
                chain.Add(aIsTip ? b : a);
            }

            // Walk backward from A (prepend to chain).
            while (true)
            {
                var tail    = chain[0];
                var tailKey = Quantise(tail);
                if (!adj.TryGetValue(tailKey, out var candidates)) break;

                int prev = -1;
                foreach (int si in candidates)
                {
                    if (used[si]) continue;
                    prev = si;
                    break;
                }
                if (prev < 0) break;

                used[prev] = true;
                var (a, b) = segs[prev];
                bool aIsTail = Quantise(a) == tailKey;
                chain.Insert(0, aIsTail ? b : a);
            }

            // Ensure CCW winding (positive signed area).
            if (SignedArea(chain) < 0f)
                chain.Reverse();

            contours.Add(chain);
        }

        return contours;
    }

    private static float SignedArea(List<Vector2> poly)
    {
        float area = 0f;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area * 0.5f;
    }
}
