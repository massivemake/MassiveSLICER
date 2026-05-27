using System.Numerics;
using System.Runtime.CompilerServices;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Angled-planar slicer. Like <see cref="PlanarSlicer"/> but cuts with planes tilted at
/// <see cref="SliceSettings.TiltAngle"/> degrees from horizontal, reducing stair-stepping
/// on inclined surfaces. Layers are still spaced by <see cref="SliceSettings.LayerHeight"/>
/// in Z so vertical material deposition height is preserved.
/// </summary>
public static class AngledPlanarSlicer
{
    private const float SnapGrid = 0.01f;

    // ── Public entry point ────────────────────────────────────────────────────

    public static Toolpath Slice(
        IReadOnlyList<Vector3[]> meshes,
        SliceSettings settings)
    {
        float ty = settings.TiltAngle  * MathF.PI / 180f;
        float tx = settings.TiltAngleX * MathF.PI / 180f;
        // Rotate Z-up normal first around Y (leans toward ±X), then around X (leans toward ±Y).
        var normal = Vector3.Normalize(new Vector3(
            MathF.Sin(ty),
            -MathF.Sin(tx) * MathF.Cos(ty),
             MathF.Cos(tx) * MathF.Cos(ty)));

        // Project every vertex onto the plane normal to find the full extent along
        // that direction. Marching in this coordinate (not world Z) ensures complete
        // mesh coverage regardless of tilt angle or mesh XY position.
        float tMin = float.MaxValue, tMax = float.MinValue;
        float xMin = float.MaxValue, xMax = float.MinValue;
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (var verts in meshes)
            foreach (var v in verts)
            {
                float t = Vector3.Dot(v, normal);
                if (t < tMin) tMin = t; if (t > tMax) tMax = t;
                if (v.X < xMin) xMin = v.X; if (v.X > xMax) xMax = v.X;
                if (v.Y < yMin) yMin = v.Y; if (v.Y > yMax) yMax = v.Y;
            }

        if (tMax <= tMin) return new Toolpath();

        // Seam ray origin: outside the mesh along SeamDirection in XY.
        // Used only to initialise the arc-length seam parameter on the first layer.
        var sd = settings.SeamDirection;
        float sdLen = sd.Length();
        if (sdLen < 1e-6f) sd = new Vector2(0f, 1f); else sd /= sdLen;
        float cx    = (xMin + xMax) * 0.5f;
        float cy    = (yMin + yMax) * 0.5f;
        float reach = (xMax - xMin + yMax - yMin) + 10f;
        var seamOrigin = new Vector2(cx + sd.X * reach, cy + sd.Y * reach);

        var   toolpath   = new Toolpath();
        float step       = tMin + settings.FirstLayerHeight;
        int   idx        = 0;
        var   prevTracks = new List<AngledContourTrack>();

        while (step < tMax - 1e-4f)
        {
            float repZ = normal.Z > 1e-6f ? step / normal.Z : step;
            var   layer = new ToolpathLayer(idx++, repZ);
            prevTracks = BuildLayer(meshes, normal, step, seamOrigin, sd, prevTracks, layer);
            if (layer.Moves.Count > 0)
                toolpath.Layers.Add(layer);
            step += settings.LayerHeight;
        }

        return toolpath;
    }

    // ── Layer construction ────────────────────────────────────────────────────

    private static List<AngledContourTrack> BuildLayer(
        IReadOnlyList<Vector3[]> meshes,
        Vector3 normal,
        float   planeD,
        Vector2 seamOrigin,
        Vector2 seamDir,
        List<AngledContourTrack> prevTracks,
        ToolpathLayer layer)
    {
        var segments = new List<(Vector3 A, Vector3 B)>(64);
        foreach (var verts in meshes)
            CollectSegments(verts, normal, planeD, segments);

        if (segments.Count == 0) return new List<AngledContourTrack>();

        var (closed, open) = ChainSegments(segments, normal);
        StitchChains(open, normal);
        var rawContours = closed;
        rawContours.AddRange(open);
        var tracks = AssignSeams(rawContours, prevTracks, seamOrigin, seamDir);

        var lastPos = new Vector3(float.NaN);
        foreach (var track in tracks)
        {
            var contour = track.Contour;
            if (contour.Count < 2) continue;

            var start = contour[0];

            if (!float.IsNaN(lastPos.X))
                layer.Moves.Add(new ToolpathMove(lastPos, start, MoveKind.Travel));

            var prev = start;
            for (int i = 1; i < contour.Count; i++)
            {
                var next = contour[i];
                layer.Moves.Add(new ToolpathMove(prev, next, MoveKind.Extrude));
                prev = next;
            }

            if (contour.Count > 2)
                layer.Moves.Add(new ToolpathMove(prev, start, MoveKind.Extrude));

            lastPos = contour[^1];
        }

        return tracks;
    }

    // ── Intersection / segment collection ─────────────────────────────────────

    private static void CollectSegments(
        Vector3[]              verts,
        Vector3                normal,
        float                  planeD,
        List<(Vector3, Vector3)> segments)
    {
        Span<Vector3> pts = stackalloc Vector3[2];

        for (int i = 0; i + 2 < verts.Length; i += 3)
        {
            var v0 = verts[i];
            var v1 = verts[i + 1];
            var v2 = verts[i + 2];

            float d0 = Vector3.Dot(v0, normal) - planeD;
            float d1 = Vector3.Dot(v1, normal) - planeD;
            float d2 = Vector3.Dot(v2, normal) - planeD;

            if (MathF.Abs(d0) < 1e-5f) d0 = d0 >= 0f ? 1e-5f : -1e-5f;
            if (MathF.Abs(d1) < 1e-5f) d1 = d1 >= 0f ? 1e-5f : -1e-5f;
            if (MathF.Abs(d2) < 1e-5f) d2 = d2 >= 0f ? 1e-5f : -1e-5f;

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
        Span<Vector3> pts, ref int count)
    {
        if (count >= 2) return;
        if (da * db >= 0f) return;

        float t = da / (da - db);
        pts[count++] = a + t * (b - a);
    }

    // ── Seam alignment ────────────────────────────────────────────────────────

    private static void AlignSeam(
        List<Vector3> contour,
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
                var a = new Vector2(contour[i].X, contour[i].Y);
                var b = new Vector2(contour[(i + 1) % n].X, contour[(i + 1) % n].Y);
                float t = ClosestT(a, b, prevSeamXY);
                var pt = a + t * (b - a);
                float dx = pt.X - prevSeamXY.X, dy = pt.Y - prevSeamXY.Y;
                float d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; bestEdge = i; bestT = t; }
            }
        }

        var pa3    = contour[bestEdge];
        var pb3    = contour[(bestEdge + 1) % n];
        var seamPt = pa3 + bestT * (pb3 - pa3);

        float d2a = (seamPt.X - pa3.X) * (seamPt.X - pa3.X) + (seamPt.Y - pa3.Y) * (seamPt.Y - pa3.Y);
        float d2b = (seamPt.X - pb3.X) * (seamPt.X - pb3.X) + (seamPt.Y - pb3.Y) * (seamPt.Y - pb3.Y);

        int insertAt = bestEdge + 1;
        if      (d2a < 1e-4f) insertAt = bestEdge;
        else if (d2b < 1e-4f) insertAt = (bestEdge + 1) % n;
        else                  contour.Insert(insertAt, seamPt);

        if (insertAt % contour.Count != 0)
        {
            var rotated = new List<Vector3>(contour.Count);
            rotated.AddRange(contour.GetRange(insertAt, contour.Count - insertAt));
            rotated.AddRange(contour.GetRange(0, insertAt));
            contour.Clear();
            contour.AddRange(rotated);
        }

        prevSeamXY = new Vector2(contour[0].X, contour[0].Y);
    }

    private static void SeamEdgeFromRay(
        List<Vector3> contour, Vector2 seamOrigin, Vector2 seamDir,
        out int edge, out float t)
    {
        var   rayDir   = -seamDir;
        float bestRayT = float.MaxValue;
        edge = 0; t = 0f;

        for (int i = 0; i < contour.Count; i++)
        {
            var a2 = new Vector2(contour[i].X, contour[i].Y);
            var b2 = new Vector2(contour[(i + 1) % contour.Count].X, contour[(i + 1) % contour.Count].Y);
            if (RaySegment(seamOrigin, rayDir, a2, b2, out float rayT, out float segS) && rayT < bestRayT)
            {
                bestRayT = rayT; edge = i; t = segS;
            }
        }
    }

    private static float ClosestT(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        float d = ab.LengthSquared();
        if (d < 1e-10f) return 0f;
        return Math.Clamp(Vector2.Dot(p - a, ab) / d, 0f, 1f);
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

    // ── Chain stitching ───────────────────────────────────────────────────────

    private static void StitchChains(List<List<Vector3>> chains, Vector3 planeNormal)
    {
        const float ClosedSq = 1.0f;

        while (true)
        {
            float best = float.MaxValue;
            int bi = -1, bj = -1, bc = -1;

            for (int i = 0; i < chains.Count; i++)
            {
                var ci = chains[i];
                if ((ci[^1] - ci[0]).LengthSquared() < ClosedSq) continue;

                for (int j = 0; j < chains.Count; j++)
                {
                    if (j == i) continue;
                    var cj = chains[j];
                    float d;
                    d = (ci[^1] - cj[0]).LengthSquared();  if (d < best) { best = d; bi = i; bj = j; bc = 0; }
                    d = (ci[^1] - cj[^1]).LengthSquared(); if (d < best) { best = d; bi = i; bj = j; bc = 1; }
                    d = (ci[0]  - cj[^1]).LengthSquared(); if (d < best) { best = d; bi = i; bj = j; bc = 2; }
                    d = (ci[0]  - cj[0]).LengthSquared();  if (d < best) { best = d; bi = i; bj = j; bc = 3; }
                }
            }

            if (bi < 0) break;

            var ca = chains[bi];
            var cb = new List<Vector3>(chains[bj]);
            switch (bc)
            {
                case 0: ca.AddRange(cb); break;
                case 1: cb.Reverse(); ca.AddRange(cb); break;
                case 2: cb.AddRange(ca); chains[bi] = ca = cb; break;
                case 3: cb.Reverse(); cb.AddRange(ca); chains[bi] = ca = cb; break;
            }
            var polyNormal = Vector3.Zero;
            int n = ca.Count;
            for (int k = 0; k < n; k++)
                polyNormal += Vector3.Cross(ca[k], ca[(k + 1) % n]);
            if (Vector3.Dot(polyNormal, planeNormal) < 0f)
                ca.Reverse();

            chains.RemoveAt(bj);
        }
    }

    // ── Topological contour extraction ───────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int, int, int) Quantise(Vector3 p)
        => ((int)MathF.Round(p.X / SnapGrid),
            (int)MathF.Round(p.Y / SnapGrid),
            (int)MathF.Round(p.Z / SnapGrid));

    private static (List<List<Vector3>>, List<List<Vector3>>) ChainSegments(
        List<(Vector3 A, Vector3 B)> segs,
        Vector3 planeNormal)
    {
        var keyToId = new Dictionary<(int, int, int), int>();
        var verts   = new List<Vector3>();
        var adj     = new List<List<int>>();

        int Weld(Vector3 p)
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
        var closed  = new List<List<Vector3>>();
        var open    = new List<List<Vector3>>();

        for (int s = 0; s < verts.Count; s++)
        {
            if (visited[s] || adj[s].Count == 0) continue;

            var  chain    = new List<Vector3>();
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
                var polyNormal = Vector3.Zero;
                int n = chain.Count;
                for (int i = 0; i < n; i++)
                    polyNormal += Vector3.Cross(chain[i], chain[(i + 1) % n]);
                if (Vector3.Dot(polyNormal, planeNormal) < 0f)
                    chain.Reverse();

                (isClosed ? closed : open).Add(chain);
            }
        }

        return (closed, open);
    }

    // ── Topology-aware seam assignment ────────────────────────────────────────

    private const float OverlapThreshold = 0.05f;

    private static List<AngledContourTrack> AssignSeams(
        List<List<Vector3>> contours,
        List<AngledContourTrack> prevTracks,
        Vector2 seamOrigin, Vector2 seamDir)
    {
        var tracks = new List<AngledContourTrack>(contours.Count);
        foreach (var raw in contours)
        {
            var contour = new List<Vector3>(raw);

            float bestScore = 0f;
            AngledContourTrack? bestParent = null;
            foreach (var prev in prevTracks)
            {
                float score = OverlapScore(prev.Contour, contour);
                if (score > bestScore) { bestScore = score; bestParent = prev; }
            }

            Vector2 seamRef = (bestParent != null && bestScore >= OverlapThreshold)
                ? bestParent.SeamXY
                : new Vector2(float.NaN, float.NaN);

            AlignSeam(contour, seamOrigin, seamDir, ref seamRef);
            tracks.Add(new AngledContourTrack(contour, seamRef));
        }
        return tracks;
    }

    // XY-projected point-in-polygon overlap via vertex sampling.
    private static float OverlapScore(List<Vector3> a, List<Vector3> b)
    {
        int aInB = 0, bInA = 0;
        foreach (var p in a) if (PointInPolygonXY(p, b)) aInB++;
        foreach (var p in b) if (PointInPolygonXY(p, a)) bInA++;
        float rA = a.Count > 0 ? (float)aInB / a.Count : 0f;
        float rB = b.Count > 0 ? (float)bInA / b.Count : 0f;
        return MathF.Max(rA, rB);
    }

    private static bool PointInPolygonXY(Vector3 p, List<Vector3> poly)
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

    // ── Per-contour seam tracking ─────────────────────────────────────────────

    private sealed class AngledContourTrack(List<Vector3> contour, Vector2 seamXY)
    {
        public readonly List<Vector3> Contour = contour;
        public readonly Vector2 SeamXY = seamXY;
    }
}
