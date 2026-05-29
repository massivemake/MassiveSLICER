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
    // -- Public entry point ----------------------------------------------------

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
        // -- Compute Z + XY extents across all meshes -------------------------
        float zMin = float.MaxValue, zMax = float.MinValue;
        float xMin = float.MaxValue, xMax = float.MinValue;
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (var verts in meshes)
            foreach (var v in verts)
            {
                if (v.Z < zMin) zMin = v.Z; if (v.Z > zMax) zMax = v.Z;
                if (v.X < xMin) xMin = v.X; if (v.X > xMax) xMax = v.X;
                if (v.Y < yMin) yMin = v.Y; if (v.Y > yMax) yMax = v.Y;
            }

        if (zMax <= zMin) return new Toolpath();

        // Seam ray: fired from outside the mesh along SeamDirection.
        // Used only to initialise the arc-length seam parameter on the first layer.
        var sd = settings.SeamDirection;
        float sdLen = sd.Length();
        if (sdLen < 1e-6f) sd = new Vector2(0f, 1f); else sd /= sdLen;
        float cx    = (xMin + xMax) * 0.5f;
        float cy    = (yMin + yMax) * 0.5f;
        float reach = (xMax - xMin + yMax - yMin) + 10f;
        var seamOrigin = new Vector2(cx + sd.X * reach, cy + sd.Y * reach);

        var toolpath   = new Toolpath();
        float z        = zMin + settings.FirstLayerHeight;
        int   idx      = 0;
        var   prevTracks = new List<ContourTrack>();
        while (z < zMax - 1e-4f)
        {
            var layer = new ToolpathLayer(idx++, z);
            prevTracks = BuildLayer(meshes, z, settings, seamOrigin, sd, prevTracks, layer);
            if (layer.Moves.Count > 0)
                toolpath.Layers.Add(layer);
            z += settings.LayerHeight;
        }

        return toolpath;
    }

    // -- Layer construction ----------------------------------------------------

    private static List<ContourTrack> BuildLayer(
        IReadOnlyList<Vector3[]> meshes,
        float z,
        SliceSettings settings,
        Vector2 seamOrigin,
        Vector2 seamDir,
        List<ContourTrack> prevTracks,
        ToolpathLayer layer)
    {
        var segments = new List<(Vector2 A, Vector2 B)>(64);
        foreach (var verts in meshes)
            CollectSegments(verts, z, segments);

        if (segments.Count == 0) return new List<ContourTrack>();

        var (closed, open) = ChainSegments(segments);
        StitchChains(open);
        var rawContours = closed;
        rawContours.AddRange(open);
        var tracks = AssignSeams(rawContours, prevTracks, seamOrigin, seamDir);

        var lastPos = new Vector2(float.NaN);
        foreach (var track in tracks)
        {
            var contour = track.Contour;
            if (contour.Count < 2) continue;

            var start = new Vector3(contour[0].X, contour[0].Y, z);

            if (!float.IsNaN(lastPos.X))
            {
                var travelFrom = new Vector3(lastPos.X, lastPos.Y, z);
                layer.Moves.Add(new ToolpathMove(travelFrom, start, MoveKind.Travel));
            }

            var prev = start;
            for (int i = 1; i < contour.Count; i++)
            {
                var next = new Vector3(contour[i].X, contour[i].Y, z);
                layer.Moves.Add(new ToolpathMove(prev, next, MoveKind.Extrude));
                prev = next;
            }

            if (contour.Count > 2)
                layer.Moves.Add(new ToolpathMove(prev, start, MoveKind.Extrude));

            lastPos = contour[^1];
        }

        return tracks;
    }

    // -- Intersection / segment collection -------------------------------------

    private static void CollectSegments(
        Vector3[] verts,
        float z,
        List<(Vector2, Vector2)> segments)
    {
        Span<Vector2> pts = stackalloc Vector2[2];

        // verts is a flat triangle soup -- every 3 entries = one triangle.
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
        if (da * db >= 0f) return; // same side -- no crossing

        float t = da / (da - db);
        pts[count++] = new Vector2(
            a.X + t * (b.X - a.X),
            a.Y + t * (b.Y - a.Y));
    }

    // -- Topological contour extraction ---------------------------------------

    // Welding tolerance: two endpoints within this distance share the same topological vertex.
    private const float SnapGrid = 0.01f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int, int) Quantise(Vector2 p)
        => ((int)MathF.Round(p.X / SnapGrid),
            (int)MathF.Round(p.Y / SnapGrid));

    /// <summary>
    /// Converts raw intersection segments into ordered closed contours using a topological graph.
    ///
    /// Pipeline (per the reference document):
    ///   1. Weld nearby endpoints into unique vertex IDs (quantised grid hash).
    ///   2. Build an adjacency graph: vertex -> [neighbour, ...].
    ///      For a manifold mesh every vertex has degree 2, making cycles unambiguous.
    ///   3. Traverse each connected component as a cycle (walk to unvisited neighbour
    ///      that isn't the vertex we came from; stop when we return to start).
    ///   4. Fix CCW winding via signed area.
    ///
    /// Limitations:
    ///   Degree-1 vertices (open mesh boundary) produce open chains handled by StitchChains.
    ///   Degree-3+ vertices (non-manifold) are resolved greedily (first available neighbour),
    ///   which may split one logical contour into two -- StitchChains merges them afterward.
    /// </summary>
    // Returns (closed cycles, open chains). Only open chains need stitching.
    // Keeping them separate prevents StitchChains from merging distinct closed contours
    // whose first↔last vertex gap equals a full edge length (always >> the 1mm threshold).
    private static (List<List<Vector2>>, List<List<Vector2>>) ChainSegments(
        List<(Vector2 A, Vector2 B)> segs)
    {
        var keyToId = new Dictionary<(int, int), int>();
        var verts   = new List<Vector2>();
        var adj     = new List<List<int>>();

        int Weld(Vector2 p)
        {
            var key = Quantise(p);
            if (!keyToId.TryGetValue(key, out int id))
            {
                id = verts.Count;
                keyToId[key] = id;
                verts.Add(p);
                adj.Add(new List<int>(2));
            }
            return id;
        }

        foreach (var (A, B) in segs)
        {
            int va = Weld(A), vb = Weld(B);
            if (va == vb) continue;
            adj[va].Add(vb);
            adj[vb].Add(va);
        }

        var visited = new bool[verts.Count];
        var closed  = new List<List<Vector2>>();
        var open    = new List<List<Vector2>>();

        for (int s = 0; s < verts.Count; s++)
        {
            if (visited[s] || adj[s].Count == 0) continue;

            var  chain    = new List<Vector2>();
            int  cur      = s, prev = -1;
            bool isClosed = false;

            while (true)
            {
                visited[cur] = true;
                chain.Add(verts[cur]);

                int next = -1;
                foreach (int nb in adj[cur])
                {
                    if (nb == prev) continue;
                    if (nb == s && chain.Count >= 3) { next = -2; isClosed = true; break; }
                    if (!visited[nb]) { next = nb; break; }
                }

                if (next < 0) break;
                prev = cur;
                cur  = next;
            }

            if (chain.Count >= 3)
            {
                if (SignedArea(chain) < 0f) chain.Reverse();
                (isClosed ? closed : open).Add(chain);
            }
        }

        return (closed, open);
    }

    private static void AlignSeam(
        List<Vector2> contour,
        Vector2 seamOrigin, Vector2 seamDir,
        ref Vector2 prevSeamXY)
    {
        if (contour.Count < 3) return;

        int n = contour.Count;
        int bestEdge;
        float bestT;

        if (float.IsNaN(prevSeamXY.X))
        {
            SeamEdgeFromRay(contour, seamOrigin, seamDir, out bestEdge, out bestT);
        }
        else
        {
            bestEdge = 0; bestT = 0f;
            float bestDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var a = contour[i];
                var b = contour[(i + 1) % n];
                float t = ClosestT(a, b, prevSeamXY);
                var pt = a + t * (b - a);
                float d = Dist2(pt, prevSeamXY);
                if (d < bestDist) { bestDist = d; bestEdge = i; bestT = t; }
            }
        }

        var pa     = contour[bestEdge];
        var pb     = contour[(bestEdge + 1) % n];
        var seamPt = pa + bestT * (pb - pa);

        int insertAt = bestEdge + 1;
        if      (Dist2(seamPt, pa) < 1e-4f) insertAt = bestEdge;
        else if (Dist2(seamPt, pb) < 1e-4f) insertAt = (bestEdge + 1) % n;
        else                                contour.Insert(insertAt, seamPt);

        if (insertAt % contour.Count != 0)
        {
            var rotated = new List<Vector2>(contour.Count);
            rotated.AddRange(contour.GetRange(insertAt, contour.Count - insertAt));
            rotated.AddRange(contour.GetRange(0, insertAt));
            contour.Clear();
            contour.AddRange(rotated);
        }

        prevSeamXY = contour[0];
    }

    private static void SeamEdgeFromRay(
        List<Vector2> contour, Vector2 seamOrigin, Vector2 seamDir,
        out int edge, out float t)
    {
        var   rayDir   = -seamDir;
        float bestRayT = float.MaxValue;
        edge = 0; t = 0f;

        for (int i = 0; i < contour.Count; i++)
        {
            var a = contour[i];
            var b = contour[(i + 1) % contour.Count];
            if (RaySegment(seamOrigin, rayDir, a, b, out float rayT, out float segS) && rayT < bestRayT)
            {
                bestRayT = rayT; edge = i; t = segS;
            }
        }
    }

    private static bool RaySegment(Vector2 origin, Vector2 dir, Vector2 a, Vector2 b,
        out float t, out float s)
    {
        var   ab  = b - a;
        float den = dir.X * ab.Y - dir.Y * ab.X;
        if (MathF.Abs(den) < 1e-9f) { t = s = 0f; return false; }
        var ao = a - origin;
        t = (ao.X * ab.Y - ao.Y * ab.X) / den;
        s = (ao.X * dir.Y - ao.Y * dir.X) / den;
        return t > -1e-4f && s >= -1e-4f && s <= 1f + 1e-4f;
    }

    // Repeatedly find the closest pair of open chain endpoints (globally) and merge them
    // until no open chains remain. "Open" = gap between first and last vertex > 1 mm.
    // No fixed distance limit -- for a single-shell object all open ends are artifacts.
    private static void StitchChains(List<List<Vector2>> chains)
    {
        const float ClosedSq = 1.0f; // < 1 mm gap = already closed

        while (true)
        {
            // Scan every pair of open endpoints across all chains.
            float best = float.MaxValue;
            int bi = -1, bj = -1, bc = -1;

            for (int i = 0; i < chains.Count; i++)
            {
                var ci = chains[i];
                if (Dist2(ci[0], ci[^1]) < ClosedSq) continue;

                for (int j = 0; j < chains.Count; j++)
                {
                    if (j == i) continue;
                    var cj = chains[j];
                    float d;
                    d = Dist2(ci[^1], cj[0]);  if (d < best) { best = d; bi = i; bj = j; bc = 0; }
                    d = Dist2(ci[^1], cj[^1]); if (d < best) { best = d; bi = i; bj = j; bc = 1; }
                    d = Dist2(ci[0],  cj[^1]); if (d < best) { best = d; bi = i; bj = j; bc = 2; }
                    d = Dist2(ci[0],  cj[0]);  if (d < best) { best = d; bi = i; bj = j; bc = 3; }
                }
            }

            if (bi < 0) break;

            var ca = chains[bi];
            var cb = new List<Vector2>(chains[bj]);
            switch (bc)
            {
                case 0: ca.AddRange(cb); break;
                case 1: cb.Reverse(); ca.AddRange(cb); break;
                case 2: cb.AddRange(ca); chains[bi] = ca = cb; break;
                case 3: cb.Reverse(); cb.AddRange(ca); chains[bi] = ca = cb; break;
            }
            if (SignedArea(ca) < 0f) ca.Reverse();
            chains.RemoveAt(bj);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dist2(Vector2 a, Vector2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static float ClosestT(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        float d = ab.LengthSquared();
        if (d < 1e-10f) return 0f;
        return Math.Clamp(Vector2.Dot(p - a, ab) / d, 0f, 1f);
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

    // -- Topology-aware seam assignment ----------------------------------------

    // Minimum overlap fraction (of the smaller contour's vertices) to consider
    // two contours on adjacent layers to be the "same" feature.
    private const float OverlapThreshold = 0.05f;

    private static List<ContourTrack> AssignSeams(
        List<List<Vector2>> contours,
        List<ContourTrack> prevTracks,
        Vector2 seamOrigin, Vector2 seamDir)
    {
        var tracks = new List<ContourTrack>(contours.Count);
        foreach (var raw in contours)
        {
            var contour = new List<Vector2>(raw);

            // Find best parent via XY overlap.
            float bestScore = 0f;
            ContourTrack? bestParent = null;
            foreach (var prev in prevTracks)
            {
                float score = OverlapScore(prev.Contour, contour);
                if (score > bestScore) { bestScore = score; bestParent = prev; }
            }

            // Birth (no parent) -> initialize seam from ray.
            // Continuous / split / merge -> project from parent seam.
            Vector2 seamRef = (bestParent != null && bestScore >= OverlapThreshold)
                ? bestParent.SeamXY
                : new Vector2(float.NaN, float.NaN);

            AlignSeam(contour, seamOrigin, seamDir, ref seamRef);
            tracks.Add(new ContourTrack(contour, seamRef));
        }
        return tracks;
    }

    // Approximate overlap ratio using vertex-in-polygon sampling.
    // Returns max(fraction of A's vertices inside B, fraction of B's inside A).
    private static float OverlapScore(List<Vector2> a, List<Vector2> b)
    {
        int aInB = 0, bInA = 0;
        foreach (var p in a) if (PointInPolygon(p, b)) aInB++;
        foreach (var p in b) if (PointInPolygon(p, a)) bInA++;
        float rA = a.Count > 0 ? (float)aInB / a.Count : 0f;
        float rB = b.Count > 0 ? (float)bInA / b.Count : 0f;
        return MathF.Max(rA, rB);
    }

    private static bool PointInPolygon(Vector2 p, List<Vector2> poly)
    {
        int n = poly.Count;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var pi = poly[i]; var pj = poly[j];
            if ((pi.Y > p.Y) != (pj.Y > p.Y) &&
                p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }
        return inside;
    }

    // -- Per-contour seam tracking ---------------------------------------------

    private sealed class ContourTrack(List<Vector2> contour, Vector2 seamXY)
    {
        public readonly List<Vector2> Contour = contour;
        public readonly Vector2 SeamXY = seamXY;
    }
}
