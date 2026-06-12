using System.Numerics;
using System.Runtime.CompilerServices;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Non-planar geodesic slicer. Computes a surface-distance field from the build plate
/// via Dijkstra on the welded mesh graph, then extracts curved 3D iso-distance contours
/// as toolpath layers.
///
/// Each move normal is the <em>geodesic distance gradient</em> at the crossing triangle --
/// the direction in which successive layers stack along the surface. Passing this to
/// TargetRotFromPathFrame (which does kukaX = -normal) places the flange on the
/// "next-layer" side of the TCP for every surface angle, so beads always deposit
/// directly onto the previous layer regardless of overhang.
/// </summary>
public static class GeodesicSlicer
{
    private const float SnapGrid = 0.01f;

    // -- Public entry point ----------------------------------------------------

    public static Toolpath Slice(IReadOnlyList<Vector3[]> meshes, SliceSettings settings)
    {
        var (verts, tris) = BuildIndexedMesh(meshes);
        if (verts.Length == 0 || tris.Length == 0) return new Toolpath();

        float zMin = float.MaxValue;
        foreach (var v in verts) if (v.Z < zMin) zMin = v.Z;

        var adj      = BuildAdjacency(verts, tris);
        var geodDist = ComputeGeodesicDistances(verts, adj, zMin + settings.LayerHeight * 0.1f);

        float maxDist = 0f;
        foreach (var d in geodDist)
            if (d < float.MaxValue / 2f && d > maxDist) maxDist = d;
        if (maxDist < settings.FirstLayerHeight) return new Toolpath();

        // Seam origin: outside mesh along SeamDirection in XY
        float xMin = float.MaxValue, xMax = float.MinValue;
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (var v in verts)
        {
            if (v.X < xMin) xMin = v.X; if (v.X > xMax) xMax = v.X;
            if (v.Y < yMin) yMin = v.Y; if (v.Y > yMax) yMax = v.Y;
        }
        var sd = settings.SeamDirection;
        float sdLen = sd.Length();
        if (sdLen < 1e-6f) sd = new Vector2(0f, 1f); else sd /= sdLen;
        float cx = (xMin + xMax) * 0.5f, cy = (yMin + yMax) * 0.5f;
        float reach = (xMax - xMin + yMax - yMin) + 10f;
        var seamOrigin = new Vector2(cx + sd.X * reach, cy + sd.Y * reach);

        var toolpath     = new Toolpath();
        float layerD     = settings.FirstLayerHeight;
        int   layerIdx   = 0;
        var   prevTracks = new List<GeoContourTrack>();

        while (layerD <= maxDist + 1e-4f)
        {
            var segments = new List<(Vector3 A, Vector3 B, Vector3 GradDir)>(64);
            CollectCrossings(verts, tris, geodDist, layerD, segments);

            if (segments.Count > 0)
            {
                var (closed, open) = ChainSegments(segments);
                StitchChains(open);
                var rawContours = closed;
                rawContours.AddRange(open);

                var tracks = AssignSeams(rawContours, prevTracks, seamOrigin, sd);
                prevTracks = tracks;

                float avgZ = 0f; int ptCount = 0;
                foreach (var t in tracks)
                    foreach (var (p, _) in t.Contour) { avgZ += p.Z; ptCount++; }
                if (ptCount > 0) avgZ /= ptCount;

                var layer = new ToolpathLayer(layerIdx++, avgZ);
                EmitMoves(tracks, layer);
                if (layer.Moves.Count > 0)
                    toolpath.Layers.Add(layer);
            }

            layerD += settings.LayerHeight;
        }

        return toolpath;
    }

    // -- Indexed mesh building -------------------------------------------------

    private static (Vector3[] verts, int[][] tris) BuildIndexedMesh(
        IReadOnlyList<Vector3[]> meshes)
    {
        var keyToId = new Dictionary<(int, int, int), int>();
        var verts   = new List<Vector3>();
        var tris    = new List<int[]>();

        foreach (var soup in meshes)
        {
            for (int i = 0; i + 2 < soup.Length; i += 3)
            {
                var v0 = soup[i]; var v1 = soup[i + 1]; var v2 = soup[i + 2];
                int i0 = Weld(v0, keyToId, verts);
                int i1 = Weld(v1, keyToId, verts);
                int i2 = Weld(v2, keyToId, verts);
                if (i0 == i1 || i1 == i2 || i0 == i2) continue;
                tris.Add([i0, i1, i2]);
            }
        }

        return (verts.ToArray(), tris.ToArray());
    }

    private static int Weld(Vector3 p, Dictionary<(int, int, int), int> map, List<Vector3> verts)
    {
        var key = ((int)MathF.Round(p.X / SnapGrid),
                   (int)MathF.Round(p.Y / SnapGrid),
                   (int)MathF.Round(p.Z / SnapGrid));
        if (!map.TryGetValue(key, out int id))
        {
            id = verts.Count;
            map[key] = id;
            verts.Add(p);
        }
        return id;
    }

    // -- Adjacency & Dijkstra --------------------------------------------------

    private static List<(int neighbor, float dist)>[] BuildAdjacency(Vector3[] verts, int[][] tris)
    {
        var adj  = new List<(int, float)>[verts.Length];
        for (int i = 0; i < verts.Length; i++) adj[i] = [];

        var seen = new HashSet<(int, int)>();
        foreach (var tri in tris)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = tri[k], b = tri[(k + 1) % 3];
                var edge = a < b ? (a, b) : (b, a);
                if (!seen.Add(edge)) continue;
                float d = (verts[a] - verts[b]).Length();
                adj[a].Add((b, d));
                adj[b].Add((a, d));
            }
        }
        return adj;
    }

    private static float[] ComputeGeodesicDistances(
        Vector3[] verts, List<(int neighbor, float dist)>[] adj, float seedThreshold)
    {
        var dist = new float[verts.Length];
        Array.Fill(dist, float.MaxValue);
        var pq = new PriorityQueue<int, float>();

        for (int i = 0; i < verts.Length; i++)
        {
            if (verts[i].Z <= seedThreshold)
            {
                dist[i] = 0f;
                pq.Enqueue(i, 0f);
            }
        }

        while (pq.TryDequeue(out int u, out float d))
        {
            if (d > dist[u]) continue;
            foreach (var (v, w) in adj[u])
            {
                float nd = d + w;
                if (nd < dist[v]) { dist[v] = nd; pq.Enqueue(v, nd); }
            }
        }

        return dist;
    }

    // -- Iso-distance crossing collection -------------------------------------
    //
    // For each crossing triangle we compute the geodesic distance GRADIENT on that
    // face instead of using the geometric face normal.  The gradient direction points
    // from the previous layer toward the next layer along the surface, so passing it
    // to TargetRotFromPathFrame (kukaX = -gradDir) keeps the flange on the "next-layer"
    // side of the TCP for every surface angle -- beads stack directly on the previous
    // layer regardless of overhang.
    //
    // Gradient formula for a linear scalar field on a triangle (with relative diffs
    // u0=0, u1=dd1, u2=dd2 for numerical stability):
    //   grad = (dd1 * cross(N, v0-v2) + dd2 * cross(N, v1-v0)) / |N|^2
    //        = (-dd1 * cross(N, e2) + dd2 * cross(N, e1)) / |N|^2
    // where N = cross(e1, e2), e1 = v1-v0, e2 = v2-v0.

    private static void CollectCrossings(
        Vector3[] verts, int[][] tris,
        float[] geodDist, float targetDist,
        List<(Vector3 A, Vector3 B, Vector3 GradDir)> segments)
    {
        const float Unreachable = float.MaxValue / 2f;
        Span<Vector3> pts = stackalloc Vector3[2];

        for (int ti = 0; ti < tris.Length; ti++)
        {
            var tri = tris[ti];
            int i0 = tri[0], i1 = tri[1], i2 = tri[2];

            float d0 = geodDist[i0], d1 = geodDist[i1], d2 = geodDist[i2];
            if (d0 > Unreachable || d1 > Unreachable || d2 > Unreachable) continue;

            float d0s = d0 - targetDist, d1s = d1 - targetDist, d2s = d2 - targetDist;

            if (MathF.Abs(d0s) < 1e-5f) d0s = d0s >= 0f ? 1e-5f : -1e-5f;
            if (MathF.Abs(d1s) < 1e-5f) d1s = d1s >= 0f ? 1e-5f : -1e-5f;
            if (MathF.Abs(d2s) < 1e-5f) d2s = d2s >= 0f ? 1e-5f : -1e-5f;

            int count = 0;
            TryCrossing(verts[i0], verts[i1], d0s, d1s, pts, ref count);
            TryCrossing(verts[i1], verts[i2], d1s, d2s, pts, ref count);
            TryCrossing(verts[i2], verts[i0], d2s, d0s, pts, ref count);

            if (count == 2)
            {
                var v0 = verts[i0]; var v1 = verts[i1]; var v2 = verts[i2];
                var e1 = v1 - v0; var e2 = v2 - v0;
                var fn = Vector3.Cross(e1, e2);
                float fnLen2 = fn.LengthSquared();

                Vector3 gradDir;
                if (fnLen2 > 1e-10f)
                {
                    float dd1 = d1 - d0, dd2 = d2 - d0;
                    var grad = (-dd1 * Vector3.Cross(fn, e2) + dd2 * Vector3.Cross(fn, e1)) / fnLen2;
                    float gLen = grad.Length();
                    gradDir = gLen > 1e-6f ? grad / gLen : Vector3.UnitZ;
                }
                else
                {
                    gradDir = Vector3.UnitZ;
                }

                segments.Add((pts[0], pts[1], gradDir));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TryCrossing(
        Vector3 a, Vector3 b, float da, float db,
        Span<Vector3> pts, ref int count)
    {
        if (count >= 2 || da * db >= 0f) return;
        float t = da / (da - db);
        pts[count++] = a + t * (b - a);
    }

    // -- Segment chaining (3D, with per-vertex averaged gradient directions) ---

    private static (List<List<(Vector3 pos, Vector3 normal)>>,
                    List<List<(Vector3 pos, Vector3 normal)>>)
        ChainSegments(List<(Vector3 A, Vector3 B, Vector3 GradDir)> segs)
    {
        var keyToId   = new Dictionary<(int, int, int), int>();
        var positions = new List<Vector3>();
        var normals   = new List<Vector3>();
        var adj       = new List<List<int>>();

        int WeldPt(Vector3 p, Vector3 n)
        {
            var key = ((int)MathF.Round(p.X / SnapGrid),
                       (int)MathF.Round(p.Y / SnapGrid),
                       (int)MathF.Round(p.Z / SnapGrid));
            if (!keyToId.TryGetValue(key, out int id))
            {
                id = positions.Count;
                keyToId[key] = id;
                positions.Add(p);
                normals.Add(n);
                adj.Add(new List<int>(2));
            }
            else
            {
                normals[id] += n;
            }
            return id;
        }

        foreach (var (A, B, N) in segs)
        {
            int va = WeldPt(A, N), vb = WeldPt(B, N);
            if (va == vb) continue;
            adj[va].Add(vb);
            adj[vb].Add(va);
        }

        for (int i = 0; i < normals.Count; i++)
        {
            float len = normals[i].Length();
            normals[i] = len > 1e-6f ? normals[i] / len : Vector3.UnitZ;
        }

        var visited = new bool[positions.Count];
        var closed  = new List<List<(Vector3, Vector3)>>();
        var open    = new List<List<(Vector3, Vector3)>>();

        for (int s = 0; s < positions.Count; s++)
        {
            if (visited[s] || adj[s].Count == 0) continue;

            var  chain    = new List<(Vector3, Vector3)>();
            int  cur      = s, prev = -1;
            bool isClosed = false;

            while (true)
            {
                visited[cur] = true;
                chain.Add((positions[cur], normals[cur]));

                int next = -1;
                foreach (int nb in adj[cur])
                {
                    if (nb == prev) continue;
                    if (nb == s && chain.Count >= 3) { next = -2; isClosed = true; break; }
                    if (!visited[nb]) { next = nb; break; }
                }
                if (next < 0) break;
                prev = cur; cur = next;
            }

            if (chain.Count >= 3)
            {
                var avgN  = Vector3.Zero;
                foreach (var (_, n) in chain) avgN += n;
                var polyN = Vector3.Zero;
                int cnt   = chain.Count;
                for (int i = 0; i < cnt; i++)
                    polyN += Vector3.Cross(chain[i].Item1, chain[(i + 1) % cnt].Item1);
                var refDir = avgN.LengthSquared() > 1e-6f ? avgN : Vector3.UnitZ;
                if (Vector3.Dot(polyN, refDir) < 0f) chain.Reverse();

                (isClosed ? closed : open).Add(chain);
            }
        }

        return (closed, open);
    }

    private static void StitchChains(List<List<(Vector3 pos, Vector3 normal)>> chains)
    {
        const float ClosedSq = 1.0f;

        while (true)
        {
            float best = float.MaxValue;
            int bi = -1, bj = -1, bc = -1;

            for (int i = 0; i < chains.Count; i++)
            {
                var ci = chains[i];
                if ((ci[^1].pos - ci[0].pos).LengthSquared() < ClosedSq) continue;

                for (int j = 0; j < chains.Count; j++)
                {
                    if (j == i) continue;
                    var cj = chains[j];
                    float d;
                    d = (ci[^1].pos - cj[0].pos ).LengthSquared(); if (d < best) { best = d; bi = i; bj = j; bc = 0; }
                    d = (ci[^1].pos - cj[^1].pos).LengthSquared(); if (d < best) { best = d; bi = i; bj = j; bc = 1; }
                    d = (ci[0].pos  - cj[^1].pos).LengthSquared(); if (d < best) { best = d; bi = i; bj = j; bc = 2; }
                    d = (ci[0].pos  - cj[0].pos ).LengthSquared(); if (d < best) { best = d; bi = i; bj = j; bc = 3; }
                }
            }

            if (bi < 0) break;

            var ca = chains[bi];
            var cb = new List<(Vector3, Vector3)>(chains[bj]);
            switch (bc)
            {
                case 0: ca.AddRange(cb); break;
                case 1: cb.Reverse(); ca.AddRange(cb); break;
                case 2: cb.AddRange(ca); chains[bi] = ca = cb; break;
                case 3: cb.Reverse(); cb.AddRange(ca); chains[bi] = ca = cb; break;
            }
            chains.RemoveAt(bj);
        }
    }

    // -- Seam assignment -------------------------------------------------------

    private const float OverlapThreshold = 0.05f;

    private static List<GeoContourTrack> AssignSeams(
        List<List<(Vector3 pos, Vector3 normal)>> contours,
        List<GeoContourTrack> prevTracks,
        Vector2 seamOrigin, Vector2 seamDir)
    {
        var tracks = new List<GeoContourTrack>(contours.Count);
        foreach (var raw in contours)
        {
            var contour = new List<(Vector3, Vector3)>(raw);

            float bestScore = 0f;
            GeoContourTrack? bestParent = null;
            foreach (var prev in prevTracks)
            {
                float score = OverlapScore(prev.Contour, contour);
                if (score > bestScore) { bestScore = score; bestParent = prev; }
            }

            Vector2 seamRef = (bestParent != null && bestScore >= OverlapThreshold)
                ? bestParent.SeamXY
                : new Vector2(float.NaN, float.NaN);

            AlignSeam(contour, seamOrigin, seamDir, ref seamRef);
            tracks.Add(new GeoContourTrack(contour, seamRef));
        }
        return tracks;
    }

    private static void AlignSeam(
        List<(Vector3 pos, Vector3 normal)> contour,
        Vector2 seamOrigin, Vector2 seamDir,
        ref Vector2 prevSeamXY)
    {
        if (contour.Count < 3) return;
        int n = contour.Count;
        int bestEdge; float bestT;

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
                var a2 = Xy(contour[i].pos);
                var b2 = Xy(contour[(i + 1) % n].pos);
                float t  = ClosestT(a2, b2, prevSeamXY);
                float d  = (a2 + t * (b2 - a2) - prevSeamXY).LengthSquared();
                if (d < bestDist) { bestDist = d; bestEdge = i; bestT = t; }
            }
        }

        var (pa, na) = contour[bestEdge];
        var (pb, nb) = contour[(bestEdge + 1) % n];
        var seamPos  = pa + bestT * (pb - pa);
        var seamNrm  = Vector3.Normalize(na + nb);
        if (float.IsNaN(seamNrm.X)) seamNrm = Vector3.UnitZ;

        float da = (Xy(seamPos) - Xy(pa)).LengthSquared();
        float db = (Xy(seamPos) - Xy(pb)).LengthSquared();

        int insertAt = bestEdge + 1;
        if      (da < 1e-4f) insertAt = bestEdge;
        else if (db < 1e-4f) insertAt = (bestEdge + 1) % n;
        else                 contour.Insert(insertAt, (seamPos, seamNrm));

        if (insertAt % contour.Count != 0)
        {
            var rot = new List<(Vector3, Vector3)>(contour.Count);
            rot.AddRange(contour.GetRange(insertAt, contour.Count - insertAt));
            rot.AddRange(contour.GetRange(0, insertAt));
            contour.Clear();
            contour.AddRange(rot);
        }

        prevSeamXY = Xy(contour[0].pos);
    }

    private static void SeamEdgeFromRay(
        List<(Vector3 pos, Vector3 normal)> contour,
        Vector2 seamOrigin, Vector2 seamDir,
        out int edge, out float t)
    {
        var   rayDir   = -seamDir;
        float bestRayT = float.MaxValue;
        edge = 0; t = 0f;
        for (int i = 0; i < contour.Count; i++)
        {
            var a2 = Xy(contour[i].pos);
            var b2 = Xy(contour[(i + 1) % contour.Count].pos);
            if (RaySegment(seamOrigin, rayDir, a2, b2, out float rayT, out float segS) && rayT < bestRayT)
            { bestRayT = rayT; edge = i; t = segS; }
        }
    }

    private static float OverlapScore(
        List<(Vector3 pos, Vector3 normal)> a,
        List<(Vector3 pos, Vector3 normal)> b)
    {
        int aInB = 0, bInA = 0;
        foreach (var (p, _) in a) if (PointInPolygonXY(p, b)) aInB++;
        foreach (var (p, _) in b) if (PointInPolygonXY(p, a)) bInA++;
        float rA = a.Count > 0 ? (float)aInB / a.Count : 0f;
        float rB = b.Count > 0 ? (float)bInA / b.Count : 0f;
        return MathF.Max(rA, rB);
    }

    private static bool PointInPolygonXY(Vector3 p, List<(Vector3 pos, Vector3 normal)> poly)
    {
        int  n      = poly.Count;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var pi = poly[i].pos; var pj = poly[j].pos;
            if ((pi.Y > p.Y) != (pj.Y > p.Y) &&
                p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }
        return inside;
    }

    // -- Move emission ---------------------------------------------------------

    private static void EmitMoves(List<GeoContourTrack> tracks, ToolpathLayer layer)
    {
        var lastPos    = new Vector3(float.NaN);
        var lastNormal = Vector3.UnitZ;

        foreach (var track in tracks)
        {
            var contour = track.Contour;
            if (contour.Count < 2) continue;

            var (startPos, startNormal) = contour[0];

            if (!float.IsNaN(lastPos.X))
                layer.Moves.Add(new ToolpathMove(lastPos, startPos, MoveKind.Travel) { Normal = lastNormal });

            var (prevPos, prevNormal) = contour[0];
            for (int i = 1; i < contour.Count; i++)
            {
                var (nextPos, nextNormal) = contour[i];
                layer.Moves.Add(new ToolpathMove(prevPos, nextPos, MoveKind.Extrude) { Normal = prevNormal });
                prevPos = nextPos; prevNormal = nextNormal;
            }

            if (contour.Count > 2)
            {
                float gapSq = (prevPos - startPos).LengthSquared();
                if (gapSq <= 1.0f)
                    layer.Moves.Add(new ToolpathMove(prevPos, startPos, MoveKind.Extrude) { Normal = prevNormal });
                // else: open contour — gap exceeds chaining threshold, skip forced closure
            }

            lastPos    = contour[^1].pos;
            lastNormal = contour[^1].normal;
        }
    }

    // -- Helpers ---------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 Xy(Vector3 v) => new(v.X, v.Y);

    private static float ClosestT(Vector2 a, Vector2 b, Vector2 p)
    {
        var   ab = b - a;
        float d  = ab.LengthSquared();
        return d < 1e-10f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / d, 0f, 1f);
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

    // -- Per-contour seam tracking ---------------------------------------------

    private sealed class GeoContourTrack(List<(Vector3 pos, Vector3 normal)> contour, Vector2 seamXY)
    {
        public readonly List<(Vector3 pos, Vector3 normal)> Contour = contour;
        public readonly Vector2 SeamXY = seamXY;
    }
}
