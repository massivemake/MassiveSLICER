using System.Numerics;
using System.Runtime.CompilerServices;
using System.Linq;
using Clipper2Lib;
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

        float[] zPositions = settings.AdaptiveLayerHeight
            ? AdaptiveLayerHeights.ComputeZPositions(meshes, zMin, zMax,
                  settings.FirstLayerHeight, settings.MinLayerHeight,
                  settings.LayerHeight, settings.AdaptiveQuality)
            : BuildUniformZPositions(zMin, zMax, settings.FirstLayerHeight, settings.LayerHeight);

        var toolpath     = new Toolpath();
        int idx          = 0;
        var prevTracks   = new List<ContourTrack>();
        ToolpathLayer? prevLayer = null;

        for (int zi = 0; zi < zPositions.Length; zi++)
        {
            float z      = zPositions[zi];
            float prevZ  = zi == 0 ? zMin : zPositions[zi - 1];
            var layer    = new ToolpathLayer(idx++, z) { Height = z - prevZ };
            prevTracks = BuildLayer(meshes, z, settings, seamOrigin, sd, prevTracks, layer);

            if (layer.Moves.Count > 0)
            {
                // Insert a connecting move from the end of the previous layer to the
                // start of this one.  A large XY jump gets a travel (stop extrusion);
                // a small jump gets an extrude stitch (keep printing through the seam).
                if (prevLayer is { } pl && pl.Moves.Count > 0)
                {
                    var endPos   = pl.Moves[^1].To;
                    var startPos = layer.Moves[0].From;
                    float dx = endPos.X - startPos.X;
                    float dy = endPos.Y - startPos.Y;
                    float xyDist = MathF.Sqrt(dx * dx + dy * dy);

                    if (xyDist > settings.BeadWidth)
                    {
                        layer.Moves.Insert(0, new ToolpathMove(endPos, startPos, MoveKind.Travel)
                            { IsLayerChange = true });
                    }
                    else if (xyDist > 0.01f || MathF.Abs(endPos.Z - startPos.Z) > 0.01f)
                    {
                        // Close enough to stitch without stopping extrusion.
                        layer.Moves.Insert(0, new ToolpathMove(endPos, startPos, MoveKind.Extrude) { IsLayerStitch = true });
                    }
                    // else: identical position (perfect seam alignment) — no move needed.
                }

                toolpath.Layers.Add(layer);
                prevLayer = layer;
            }
        }

        return toolpath;
    }

    // -- Z position helpers ----------------------------------------------------

    private static float[] BuildUniformZPositions(float zMin, float zMax, float firstH, float layerH)
    {
        var positions = new List<float>();
        for (float z = zMin + firstH; z < zMax - 1e-4f; z += layerH)
            positions.Add(z);
        return [.. positions];
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
        // ── Stage 1: raw intersection segments ───────────────────────────────
        var normalLookup = settings.OverhangOrientation
            ? new List<(Vector2 pos, Vector3 normal)>()
            : null;

        var perMeshSegs = new List<List<(Vector2 A, Vector2 B)>>(meshes.Count);
        foreach (var verts in meshes)
        {
            var segs = new List<(Vector2, Vector2)>(64);
            CollectSegments(verts, z, segs, normalLookup);
            if (segs.Count > 0) perMeshSegs.Add(segs);
        }
        if (perMeshSegs.Count == 0) return new List<ContourTrack>();

        // ── Stage 2: chain by endpoint proximity (per mesh) ─────────────────
        // Adjacent segments from a manifold mesh share an endpoint to floating-point
        // precision. A greedy nearest-neighbour walk is sufficient and avoids all the
        // graph/degree-3+/pruning machinery that caused corner artifacts.
        var rawContours = new List<List<Vector2>>();
        foreach (var segs in perMeshSegs)
            rawContours.AddRange(ChainByProximity(segs));

        // ── Stage 3: nesting depth + contour offset + seam ───────────────────
        if (rawContours.Count == 0) return new List<ContourTrack>();

        // Determine nesting depth via point-in-polygon so outer (even depth) and
        // hole (odd depth) contours can be distinguished, then orient and offset each.
        // Clipper2 InflatePaths contracts a CCW path with -delta and a CW path with
        // +delta, so holes need flipped delta to move their boundary into the material.
        //
        // Robustness: vote with three sample points (first, middle, last vertex) rather
        // than a single vertex so that a vertex landing on another contour's boundary
        // (common in non-manifold / disconnected-shell models at shared Z levels)
        // doesn't flip the depth count for the whole contour.
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
        var insetClosed   = new List<bool>(nc);
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
                {
                    // Open mesh boundary chains have a large gap between first and last vertex.
                    // Use the same 1mm² threshold as ChainByProximity to detect closure.
                    bool closed = Dist2(ol[0], ol[^1]) <= 1.0f;
                    insetContours.Add(simpTol > 0f ? SimplifyContour2D(ol, simpTol) : ol);
                    insetClosed.Add(closed);
                }
            }
            else
            {
                float delta   = isHole ? -halfBead : halfBead;
                var   results = InsetContour2D(oriented, delta);
                if (results.Count >= 1)
                {
                    foreach (var r in results)
                        if (r.Count >= 3)
                        {
                            insetContours.Add(simpTol > 0f ? SimplifyContour2D(r, simpTol) : r);
                            insetClosed.Add(true); // Clipper2 output is always a closed polygon
                        }
                }
            }
        }
        if (insetContours.Count == 0) return new List<ContourTrack>();

        var tracks = AssignSeams(insetContours, insetClosed, prevTracks, seamOrigin, seamDir);

        // Assign per-vertex surface normals for overhang orientation.
        if (normalLookup != null && normalLookup.Count > 0)
        {
            float maxTiltRad = settings.MaxOverhangTiltDeg * (MathF.PI / 180f);
            foreach (var track in tracks)
            {
                track.Normals = new List<Vector3>(track.Contour.Count);
                foreach (var pt in track.Contour)
                    track.Normals.Add(ClampNormalTilt(NearestNormal(pt, normalLookup), maxTiltRad));
            }
        }

        EmitContours(tracks, z, layer, settings.ZigZagSeam, layer.Index);
        return tracks;
    }

    // Emits contours as extrude loops with travel moves between them.
    // When zigZag is true, open contours on odd-indexed layers are printed end→start
    // instead of start→end, eliminating the long return travel on panel-style prints.
    private static void EmitContours(IEnumerable<ContourTrack> tracks, float z, ToolpathLayer layer,
                                     bool zigZag = false, int layerIndex = 0)
    {
        var lastPos = new Vector2(float.NaN);
        foreach (var track in tracks)
        {
            var  c        = track.Contour;
            bool isClosed = track.IsClosed;
            bool reversed = zigZag && !isClosed && (layerIndex % 2 == 1);
            int  n        = c.Count;

            Vector2? first      = null;
            Vector3  firstNorm  = Vector3.Zero;
            Vector3  prev       = default;
            int      count      = 0;
            for (int vi = 0; vi < n; vi++)
            {
                int     ci2  = reversed ? n - 1 - vi : vi;
                var     v    = c[ci2];
                var     p    = new Vector3(v.X, v.Y, z);
                Vector3 norm = track.Normals != null ? track.Normals[ci2] : Vector3.Zero;
                if (count == 0)
                {
                    first     = v;
                    firstNorm = norm;
                    if (!float.IsNaN(lastPos.X))
                        layer.Moves.Add(new ToolpathMove(new Vector3(lastPos.X, lastPos.Y, z), p, MoveKind.Travel));
                }
                else
                {
                    layer.Moves.Add(new ToolpathMove(prev, p, MoveKind.Extrude) { Normal = norm });
                }
                prev = p; count++;
            }
            if (count > 2 && first.HasValue && isClosed)
                layer.Moves.Add(new ToolpathMove(prev, new Vector3(first.Value.X, first.Value.Y, z), MoveKind.Extrude)
                    { Normal = firstNorm });
            if (count > 0)
                lastPos = new Vector2(prev.X, prev.Y);
        }
    }

    // -- Intersection / segment collection -------------------------------------

    private static void CollectSegments(
        Vector3[] verts,
        float z,
        List<(Vector2, Vector2)> segments,
        List<(Vector2 pos, Vector3 normal)>? normalLookup = null)
    {
        Span<Vector2> pts = stackalloc Vector2[2];

        // verts is a flat triangle soup -- every 3 entries = one triangle.
        for (int i = 0; i + 2 < verts.Length; i += 3)
        {
            var v0 = verts[i];
            var v1 = verts[i + 1];
            var v2 = verts[i + 2];

            float d0 = v0.Z - z;
            float d1 = v1.Z - z;
            float d2 = v2.Z - z;

            // Push nearly-on-plane vertices slightly off to avoid degenerate intersections.
            if (MathF.Abs(d0) < 1e-5f) d0 = d0 >= 0f ? 1e-5f : -1e-5f;
            if (MathF.Abs(d1) < 1e-5f) d1 = d1 >= 0f ? 1e-5f : -1e-5f;
            if (MathF.Abs(d2) < 1e-5f) d2 = d2 >= 0f ? 1e-5f : -1e-5f;

            int count = 0;
            TryEdge(v0, v1, d0, d1, pts, ref count);
            TryEdge(v1, v2, d1, d2, pts, ref count);
            TryEdge(v2, v0, d2, d0, pts, ref count);

            if (count == 2)
            {
                segments.Add((pts[0], pts[1]));
                if (normalLookup != null)
                {
                    // Gradient of Z-height on the triangle face — the direction in which
                    // successive layers stack along the surface.  Same formula as the geodesic
                    // slicer's distance-field gradient, but using vertex Z values as distances.
                    // Vertical walls → (0,0,1) (no tilt), 45° slope → 45° tilt, etc.
                    var e1 = v1 - v0; var e2 = v2 - v0;
                    var fn = Vector3.Cross(e1, e2);
                    float fnLen2 = fn.LengthSquared();
                    Vector3 gradDir;
                    if (fnLen2 > 1e-10f)
                    {
                        float dz1 = v1.Z - v0.Z, dz2 = v2.Z - v0.Z;
                        var grad = (-dz1 * Vector3.Cross(fn, e2) + dz2 * Vector3.Cross(fn, e1)) / fnLen2;
                        float gLen = grad.Length();
                        gradDir = gLen > 1e-6f ? grad / gLen : Vector3.UnitZ;
                    }
                    else
                    {
                        gradDir = Vector3.UnitZ;
                    }
                    normalLookup.Add(((pts[0] + pts[1]) * 0.5f, gradDir));
                }
            }
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

    // -- Contour extraction ---------------------------------------------------

    // Chains raw intersection segments into contours by greedily connecting nearest endpoints.
    // Extends from BOTH the head and tail so a chain grows in both directions regardless of
    // which direction the seed segment happened to be oriented. This prevents open-boundary
    // meshes (e.g. a split cube) from producing split chains that look like doubled walls.
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

            bool anyProgress = true;
            while (anyProgress)
            {
                anyProgress = false;

                // Extend from tail
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
                    if (bi >= 0 && best <= 1.0f)
                    {
                        used[bi] = true;
                        chain.Add(flip ? segs[bi].A : segs[bi].B);
                        anyProgress = true;
                    }
                }

                // Extend from head
                {
                    var   head = chain[0];
                    float best = float.MaxValue;
                    int   bi   = -1;
                    bool  flip = false;
                    for (int i = 0; i < n; i++)
                    {
                        if (used[i]) continue;
                        float dA = Dist2(head, segs[i].A);
                        float dB = Dist2(head, segs[i].B);
                        if (dA < best) { best = dA; bi = i; flip = false; } // A≈head → prepend B
                        if (dB < best) { best = dB; bi = i; flip = true;  } // B≈head → prepend A
                    }
                    if (bi >= 0 && best <= 1.0f)
                    {
                        used[bi] = true;
                        chain.Insert(0, flip ? segs[bi].A : segs[bi].B);
                        anyProgress = true;
                    }
                }
            }

            if (chain.Count >= 3)
                contours.Add(chain);
        }

        return contours;
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
        List<bool>          closedFlags,
        List<ContourTrack>  prevTracks,
        Vector2 seamOrigin, Vector2 seamDir)
    {
        var tracks = new List<ContourTrack>(contours.Count);
        for (int i = 0; i < contours.Count; i++)
        {
            var raw     = contours[i];
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

            if (closedFlags[i])
                AlignSeam(contour, seamOrigin, seamDir, ref seamRef);
            tracks.Add(new ContourTrack(contour, seamRef, closedFlags[i]));
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

    private sealed class ContourTrack(List<Vector2> contour, Vector2 seamXY, bool isClosed)
    {
        public readonly List<Vector2>  Contour  = contour;
        public readonly Vector2        SeamXY   = seamXY;
        public readonly bool           IsClosed = isClosed;
        // Per-vertex surface normals from mesh face intersection. Null = use layer.PlaneNormal.
        public List<Vector3>?          Normals;
    }

    // -- Clipper2 contour offset --------------------------------------------------

    // Outer (CCW) contours contract with delta = +halfBead → Clipper receives -halfBead.
    // Hole (CW) contours contract with delta = -halfBead → Clipper receives +halfBead.
    // Callers must orient contours and choose delta sign before calling here.
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

    // -- Douglas-Peucker contour simplification --------------------------------

    // Removes the intermediate collinear vertices Clipper2 adds on straight segments,
    // keeping only points that deviate more than `tolerance` from the simplified line.
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

    // -- Overhang orientation helpers ------------------------------------------

    private static Vector3 NearestNormal(Vector2 pt, List<(Vector2 pos, Vector3 normal)> lookup)
    {
        float best   = float.MaxValue;
        var   result = Vector3.UnitZ;
        foreach (var (pos, normal) in lookup)
        {
            float d = Dist2(pt, pos);
            if (d < best) { best = d; result = normal; }
        }
        return result;
    }

    // Clamps the normal so that its angle from straight-down (+Z) does not exceed maxTiltRad.
    // This prevents the robot from tilting to unreachable configurations on near-vertical or
    // inverted surfaces.
    private static Vector3 ClampNormalTilt(Vector3 n, float maxTiltRad)
    {
        float minZ = MathF.Cos(maxTiltRad); // e.g. cos(45°) ≈ 0.707
        if (n.Z >= minZ) return Vector3.Normalize(n);
        // Tilt exceeds limit — keep XY direction, clamp Z up to minZ.
        var   xy      = new Vector2(n.X, n.Y);
        float xyLen   = xy.Length();
        if (xyLen < 1e-6f) return Vector3.UnitZ;
        float xyTarget = MathF.Sqrt(MathF.Max(0f, 1f - minZ * minZ));
        return Vector3.Normalize(new Vector3(xy.X / xyLen * xyTarget, xy.Y / xyLen * xyTarget, minZ));
    }
}
