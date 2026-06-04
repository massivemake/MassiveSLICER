using System.Numerics;
using System.Runtime.CompilerServices;
using System.Linq;
using Clipper2Lib;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Angled-planar slicer. Like <see cref="PlanarSlicer"/> but cuts with planes tilted at
/// <see cref="SliceSettings.TiltAngle"/> / <see cref="SliceSettings.TiltAngleX"/> degrees
/// from horizontal. Contours are projected to a plane-local 2D frame for chaining and
/// Clipper2 bead-width offsetting, then unprojected back to 3D for move emission.
/// </summary>
public static class AngledPlanarSlicer
{
    // -- Public entry point ----------------------------------------------------

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

        // Local 2D frame in the cutting plane.
        // u = cross(worldY, normal) → "x-slope" direction; zero Z for pure Y-tilt.
        // v = cross(normal, u)     → "y-slope" direction; equals worldY for pure Y-tilt.
        // Both are unit vectors perpendicular to normal, so projecting to (u,v) preserves distances.
        var worldY = new Vector3(0f, 1f, 0f);
        var u = Vector3.Normalize(Vector3.Cross(worldY, normal));
        var v = Vector3.Cross(normal, u);

        // Extent along the plane normal and in world XY (for seam ray origin).
        float tMin = float.MaxValue, tMax = float.MinValue;
        float xMin = float.MaxValue, xMax = float.MinValue;
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (var verts in meshes)
            foreach (var vert in verts)
            {
                float t = Vector3.Dot(vert, normal);
                if (t < tMin) tMin = t; if (t > tMax) tMax = t;
                if (vert.X < xMin) xMin = vert.X; if (vert.X > xMax) xMax = vert.X;
                if (vert.Y < yMin) yMin = vert.Y; if (vert.Y > yMax) yMax = vert.Y;
            }

        if (tMax <= tMin) return new Toolpath();

        var sd = settings.SeamDirection;
        float sdLen = sd.Length();
        if (sdLen < 1e-6f) sd = new Vector2(0f, 1f); else sd /= sdLen;
        float cx    = (xMin + xMax) * 0.5f;
        float cy    = (yMin + yMax) * 0.5f;
        float reach = (xMax - xMin + yMax - yMin) + 10f;
        var seamOriginXY = new Vector2(cx + sd.X * reach, cy + sd.Y * reach);

        // Project seam direction to plane-local once — independent of planeD.
        var sd3d = new Vector3(sd.X, sd.Y, 0f);
        sd3d -= Vector3.Dot(sd3d, normal) * normal;
        float sd3dLen = sd3d.Length();
        if (sd3dLen < 1e-6f) sd3d = u; else sd3d /= sd3dLen;
        var seamDirLocal = new Vector2(Vector3.Dot(sd3d, u), Vector3.Dot(sd3d, v));

        var   toolpath   = new Toolpath();
        float step       = tMin + settings.FirstLayerHeight;
        int   idx        = 0;
        var   prevTracks = new List<ContourTrack>();

        while (step < tMax - 1e-4f)
        {
            // origin = closest point on this plane to the world origin.
            var origin = normal * step;

            // Project seam ray origin to plane-local (depends on planeD via origin).
            var seamOriginLocal = ToLocal(seamOriginXY, normal, step, origin, u, v);

            float repZ = normal.Z > 1e-6f ? step / normal.Z : step;
            var   layer = new ToolpathLayer(idx++, repZ) { PlaneNormal = normal };

            prevTracks = BuildLayer(meshes, normal, step, origin, u, v,
                seamOriginLocal, seamDirLocal, settings, prevTracks, layer);

            if (layer.Moves.Count > 0)
                toolpath.Layers.Add(layer);
            step += settings.LayerHeight;
        }

        return toolpath;
    }

    // Projects a world-XY point to plane-local (u,v) by solving the plane equation for Z.
    private static Vector2 ToLocal(Vector2 xy, Vector3 normal, float planeD,
        Vector3 origin, Vector3 u, Vector3 v)
    {
        float sz = MathF.Abs(normal.Z) > 1e-6f
            ? (planeD - normal.X * xy.X - normal.Y * xy.Y) / normal.Z
            : origin.Z;
        var rel = new Vector3(xy.X, xy.Y, sz) - origin;
        return new Vector2(Vector3.Dot(rel, u), Vector3.Dot(rel, v));
    }

    // -- Layer construction ----------------------------------------------------

    private static List<ContourTrack> BuildLayer(
        IReadOnlyList<Vector3[]> meshes,
        Vector3 normal, float planeD,
        Vector3 origin, Vector3 u, Vector3 v,
        Vector2 seamOrigin2d, Vector2 seamDir2d,
        SliceSettings settings,
        List<ContourTrack> prevTracks,
        ToolpathLayer layer)
    {
        // ── Stage 1: collect 3D intersection segments, project to plane-local 2D ─
        var perMeshSegs = new List<List<(Vector2 A, Vector2 B)>>(meshes.Count);
        Span<Vector3> buf = stackalloc Vector3[2];
        foreach (var verts in meshes)
        {
            var segs = new List<(Vector2, Vector2)>(64);
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                var v0 = verts[i]; var v1 = verts[i + 1]; var v2 = verts[i + 2];
                float d0 = Vector3.Dot(v0, normal) - planeD;
                float d1 = Vector3.Dot(v1, normal) - planeD;
                float d2 = Vector3.Dot(v2, normal) - planeD;
                if (MathF.Abs(d0) < 1e-5f) d0 = d0 >= 0f ? 1e-5f : -1e-5f;
                if (MathF.Abs(d1) < 1e-5f) d1 = d1 >= 0f ? 1e-5f : -1e-5f;
                if (MathF.Abs(d2) < 1e-5f) d2 = d2 >= 0f ? 1e-5f : -1e-5f;
                int count = 0;
                TryEdge(v0, v1, d0, d1, buf, ref count);
                TryEdge(v1, v2, d1, d2, buf, ref count);
                TryEdge(v2, v0, d2, d0, buf, ref count);
                if (count == 2)
                {
                    var relA = buf[0] - origin;
                    var relB = buf[1] - origin;
                    segs.Add((
                        new Vector2(Vector3.Dot(relA, u), Vector3.Dot(relA, v)),
                        new Vector2(Vector3.Dot(relB, u), Vector3.Dot(relB, v))));
                }
            }
            if (segs.Count > 0) perMeshSegs.Add(segs);
        }
        if (perMeshSegs.Count == 0) return new List<ContourTrack>();

        // ── Stage 2: chain by endpoint proximity in 2D ───────────────────────
        var rawContours = new List<List<Vector2>>();
        foreach (var segs in perMeshSegs)
            rawContours.AddRange(ChainByProximity(segs));

        // ── Stage 3: nesting depth + bead-width offset + seam ────────────────
        if (rawContours.Count == 0) return new List<ContourTrack>();

        int nc = rawContours.Count;
        var depths = new int[nc];
        for (int i = 0; i < nc; i++)
        {
            var ci = rawContours[i];
            if (ci.Count == 0) continue;
            var samples = new[] { ci[0], ci[ci.Count / 2], ci[ci.Count - 1] };
            for (int j = 0; j < nc; j++)
            {
                if (i == j) continue;
                int hits = 0;
                foreach (var s in samples) if (PointInPolygon(s, rawContours[j])) hits++;
                if (hits >= 2) depths[i]++;
            }
        }

        float halfBead = settings.BeadWidth * 0.5f;
        float simpTol  = settings.SimplificationTolerance;
        var insetContours = new List<List<Vector2>>(nc);
        for (int ci = 0; ci < nc; ci++)
        {
            var  c      = rawContours[ci];
            bool isHole = depths[ci] % 2 != 0;

            bool wantCCW = !isHole;
            bool isCCW   = SignedArea(c) > 0f;
            IReadOnlyList<Vector2> oriented;
            if (wantCCW == isCCW) oriented = c;
            else { var r = new List<Vector2>(c); r.Reverse(); oriented = r; }

            if (settings.DisableContourOffset)
            {
                var ol = oriented is List<Vector2> ol2 ? ol2 : oriented.ToList();
                if (ol.Count >= 3)
                    insetContours.Add(simpTol > 0f ? SimplifyContour2D(ol, simpTol) : ol);
            }
            else
            {
                float delta   = isHole ? -halfBead : halfBead;
                var   results = InsetContour2D(oriented, delta);
                foreach (var r in results)
                    if (r.Count >= 3)
                        insetContours.Add(simpTol > 0f ? SimplifyContour2D(r, simpTol) : r);
            }
        }
        if (insetContours.Count == 0) return new List<ContourTrack>();

        var tracks = AssignSeams(insetContours, prevTracks, seamOrigin2d, seamDir2d);
        EmitContours(tracks.Select(t => (IEnumerable<Vector2>)t.Contour),
            origin, u, v, normal, layer);
        return tracks;
    }

    // Unprojects 2D plane-local contours to 3D and emits toolpath moves.
    private static void EmitContours(
        IEnumerable<IEnumerable<Vector2>> contours,
        Vector3 origin, Vector3 u, Vector3 v,
        Vector3 normal,
        ToolpathLayer layer)
    {
        var lastPos = new Vector3(float.NaN);
        foreach (var c in contours)
        {
            Vector3? first = null;
            Vector3 prev = default;
            int count = 0;
            foreach (var p2d in c)
            {
                var p3d = origin + p2d.X * u + p2d.Y * v;
                if (count == 0)
                {
                    first = p3d;
                    if (!float.IsNaN(lastPos.X))
                        layer.Moves.Add(new ToolpathMove(lastPos, p3d, MoveKind.Travel) { Normal = normal });
                }
                else
                {
                    layer.Moves.Add(new ToolpathMove(prev, p3d, MoveKind.Extrude) { Normal = normal });
                }
                prev = p3d; count++;
            }
            if (count > 2 && first.HasValue)
            {
                // Always close the loop. Clipper2 polygons have a genuine last→first edge
                // that can be longer than 1mm, so capping on distance caused a visible gap.
                // Only skip the closing move when the gap is effectively zero (first == last).
                float gapSq = (prev - first.Value).LengthSquared();
                if (gapSq > 1e-8f)
                    layer.Moves.Add(new ToolpathMove(prev, first.Value, MoveKind.Extrude) { Normal = normal });
                lastPos = first.Value;
            }
            else if (count > 0)
            {
                lastPos = prev;
            }
        }
    }

    // -- Intersection / segment collection (3D) --------------------------------

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

    // -- Contour chaining (2D) -------------------------------------------------

    // Greedy nearest-endpoint walk — identical logic to PlanarSlicer.ChainByProximity.
    private static List<List<Vector2>> ChainByProximity(List<(Vector2 A, Vector2 B)> segs)
    {
        int n = segs.Count;
        var used     = new bool[n];
        var contours = new List<List<Vector2>>();

        for (int start = 0; start < n; start++)
        {
            if (used[start]) continue;
            used[start] = true;

            var chain = new List<Vector2> { segs[start].A, segs[start].B };

            while (true)
            {
                var   tail = chain[^1];
                float best = float.MaxValue;
                int   bi   = -1;
                bool  flip = false;

                for (int i = 0; i < n; i++)
                {
                    if (used[i]) continue;
                    float dA = Dist2(tail, segs[i].A);
                    float dB = Dist2(tail, segs[i].B);
                    if (dA < best) { best = dA; bi = i; flip = false; }
                    if (dB < best) { best = dB; bi = i; flip = true;  }
                }

                if (bi < 0 || best > 1.0f) break;

                used[bi] = true;
                chain.Add(flip ? segs[bi].A : segs[bi].B);
            }

            if (chain.Count >= 3)
                contours.Add(chain);
        }

        return contours;
    }

    // -- Clipper2 contour offset (2D) ------------------------------------------

    private static List<List<Vector2>> InsetContour2D(IReadOnlyList<Vector2> contour, float delta)
    {
        var path = new PathD(contour.Count);
        foreach (var p in contour)
            path.Add(new PointD(p.X, p.Y));
        var result = Clipper.InflatePaths(
            new PathsD { path }, -delta,
            JoinType.Miter, EndType.Polygon, miterLimit: 3.0);
        return result
            .Select(r => r.Select(p => new Vector2((float)p.x, (float)p.y)).ToList())
            .ToList();
    }

    // -- Douglas-Peucker simplification (2D) -----------------------------------

    private static List<Vector2> SimplifyContour2D(List<Vector2> pts, float tolerance)
    {
        int n = pts.Count;
        if (n <= 3) return pts;
        float tolSq = tolerance * tolerance;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        DPReduce2D(pts, 0, n - 1, tolSq, keep);
        var result = new List<Vector2>(n);
        for (int i = 0; i < n; i++)
            if (keep[i]) result.Add(pts[i]);
        return result.Count >= 3 ? result : pts;
    }

    private static void DPReduce2D(IReadOnlyList<Vector2> pts, int lo, int hi,
        float tolSq, bool[] keep)
    {
        if (hi - lo < 2) return;
        float abx = pts[hi].X - pts[lo].X, aby = pts[hi].Y - pts[lo].Y;
        float abLen2 = abx * abx + aby * aby;
        float maxDSq = 0; int maxI = lo + 1;
        for (int i = lo + 1; i < hi; i++)
        {
            float cx = pts[i].X - pts[lo].X, cy = pts[i].Y - pts[lo].Y;
            float dSq = abLen2 < 1e-10f
                ? cx * cx + cy * cy
                : (cx * aby - cy * abx) * (cx * aby - cy * abx) / abLen2;
            if (dSq > maxDSq) { maxDSq = dSq; maxI = i; }
        }
        if (maxDSq <= tolSq) return;
        keep[maxI] = true;
        DPReduce2D(pts, lo, maxI, tolSq, keep);
        DPReduce2D(pts, maxI, hi, tolSq, keep);
    }

    // -- Seam alignment (2D) ---------------------------------------------------

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

    // -- Topology-aware seam assignment (2D) -----------------------------------

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

            float bestScore = 0f;
            ContourTrack? bestParent = null;
            foreach (var prev in prevTracks)
            {
                float score = OverlapScore(prev.Contour, contour);
                if (score > bestScore) { bestScore = score; bestParent = prev; }
            }

            Vector2 seamRef = (bestParent != null && bestScore >= OverlapThreshold)
                ? bestParent.SeamXY
                : new Vector2(float.NaN, float.NaN);

            AlignSeam(contour, seamOrigin, seamDir, ref seamRef);
            tracks.Add(new ContourTrack(contour, seamRef));
        }
        return tracks;
    }

    private static float OverlapScore(List<Vector2> a, List<Vector2> b)
    {
        int aInB = 0, bInA = 0;
        foreach (var p in a) if (PointInPolygon(p, b)) aInB++;
        foreach (var p in b) if (PointInPolygon(p, a)) bInA++;
        float rA = a.Count > 0 ? (float)aInB / a.Count : 0f;
        float rB = b.Count > 0 ? (float)bInA / b.Count : 0f;
        return MathF.Max(rA, rB);
    }

    // -- Geometry helpers ------------------------------------------------------

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

    // -- Per-contour seam tracking ---------------------------------------------

    private sealed class ContourTrack(List<Vector2> contour, Vector2 seamXY)
    {
        public readonly List<Vector2> Contour = contour;
        public readonly Vector2 SeamXY = seamXY;
    }
}
