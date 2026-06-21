using System.Numerics;
using System.Runtime.CompilerServices;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Seam alignment to user guides and travel-move optimization between contours.
/// </summary>
public static class ContourSeamPlanner
{
    /// <summary>Each printed-line crossing counts as this many mm of equivalent travel distance.</summary>
    private const float CrossingPenaltyMm = 25f;

    public static Vector2 NearestGuideToContour(IReadOnlyList<Vector2> contour, IReadOnlyList<Vector2> guides)
    {
        if (guides.Count == 0) return Vector2.Zero;

        Vector2 best = guides[0];
        float bestDist = float.MaxValue;
        foreach (var guide in guides)
        {
            float d = MinDistanceToContour(contour, guide);
            if (d < bestDist) { bestDist = d; best = guide; }
        }
        return best;
    }

    public static void AlignSeamToGuide(List<Vector2> contour, Vector2 guideXY, ref Vector2 prevSeamXY)
    {
        if (contour.Count < 3) return;

        int n = contour.Count;
        int bestEdge;
        float bestT;

        if (float.IsNaN(prevSeamXY.X))
        {
            bestEdge = 0;
            bestT = 0f;
            float bestDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var a = contour[i];
                var b = contour[(i + 1) % n];
                float t = ClosestT(a, b, guideXY);
                var pt = a + t * (b - a);
                float d = Dist2(pt, guideXY);
                if (d < bestDist) { bestDist = d; bestEdge = i; bestT = t; }
            }
        }
        else
        {
            bestEdge = 0;
            bestT = 0f;
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

        InsertSeamAndRotate(contour, bestEdge, bestT, ref prevSeamXY);
    }

    public static void AlignSeamFromRay(
        List<Vector2> contour, Vector2 seamOrigin, Vector2 seamDir, ref Vector2 prevSeamXY)
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
            bestEdge = 0;
            bestT = 0f;
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

        InsertSeamAndRotate(contour, bestEdge, bestT, ref prevSeamXY);
    }

    public static void EmitOptimizedContours(
        IReadOnlyList<PlanarSlicer.ContourTrack> tracks,
        float z,
        ToolpathLayer layer,
        bool zigZag,
        int layerIndex)
    {
        var remaining = tracks.ToList();
        var printed   = new List<(Vector2 a, Vector2 b)>();
        var lastPos   = new Vector2(float.NaN, float.NaN);

        while (remaining.Count > 0)
        {
            int bestTrack = 0;
            int bestEntry = 0;
            bool bestReverse = false;
            float bestCost = float.MaxValue;

            for (int ti = 0; ti < remaining.Count; ti++)
            {
                var track = remaining[ti];
                foreach (var (entryIdx, reverse) in EntryCandidates(track))
                {
                    var entryPt = EntryPoint(track.Contour, track.IsClosed, entryIdx, reverse);
                    float dist = float.IsNaN(lastPos.X)
                        ? 0f
                        : MathF.Sqrt(Dist2(lastPos, entryPt));
                    int crossings = float.IsNaN(lastPos.X)
                        ? 0
                        : CountCrossings(lastPos, entryPt, printed);
                    float cost = dist + crossings * CrossingPenaltyMm;
                    if (cost < bestCost)
                    {
                        bestCost    = cost;
                        bestTrack   = ti;
                        bestEntry   = entryIdx;
                        bestReverse = reverse;
                    }
                }
            }

            var chosen = remaining[bestTrack];
            remaining.RemoveAt(bestTrack);

            var contour = new List<Vector2>(chosen.Contour);
            List<Vector3>? normals = chosen.Normals?.ToList();
            PrepareContourEntry(contour, normals, chosen.IsClosed, bestEntry, bestReverse);

            bool reversed = zigZag && !chosen.IsClosed && (layerIndex % 2 == 1);
            if (reversed) ReverseContour(contour, normals);

            EmitSingleContour(contour, normals, chosen.IsClosed, z, layer, ref lastPos, printed, reversed);
        }
    }

    private static IEnumerable<(int entryIdx, bool reverse)> EntryCandidates(PlanarSlicer.ContourTrack track)
    {
        if (track.Contour.Count == 0) yield break;
        if (track.IsClosed)
        {
            for (int i = 0; i < track.Contour.Count; i++)
                yield return (i, false);
        }
        else
        {
            yield return (0, false);
            yield return (track.Contour.Count - 1, true);
        }
    }

    private static Vector2 EntryPoint(IReadOnlyList<Vector2> contour, bool isClosed, int entryIdx, bool reverse)
    {
        if (contour.Count == 0) return Vector2.Zero;
        if (!isClosed && reverse) return contour[^1];
        return contour[Math.Clamp(entryIdx, 0, contour.Count - 1)];
    }

    private static void PrepareContourEntry(
        List<Vector2> contour, List<Vector3>? normals, bool isClosed, int entryIdx, bool reverse)
    {
        if (contour.Count == 0) return;
        if (isClosed && entryIdx % contour.Count != 0)
            RotateClosedContour(contour, normals, entryIdx);
        else if (!isClosed && reverse)
            ReverseContour(contour, normals);
    }

    private static void EmitSingleContour(
        IReadOnlyList<Vector2> c,
        IReadOnlyList<Vector3>? normals,
        bool isClosed,
        float z,
        ToolpathLayer layer,
        ref Vector2 lastPos,
        List<(Vector2 a, Vector2 b)> printed,
        bool reversed)
    {
        int n = c.Count;
        Vector2? first = null;
        Vector3 firstNorm = Vector3.Zero;
        Vector3 prev = default;
        int count = 0;

        for (int vi = 0; vi < n; vi++)
        {
            int ci2 = reversed ? n - 1 - vi : vi;
            var v = c[ci2];
            var p = new Vector3(v.X, v.Y, z);
            Vector3 norm = normals is not null ? normals[ci2] : Vector3.Zero;

            if (count == 0)
            {
                first = v;
                firstNorm = norm;
                if (!float.IsNaN(lastPos.X))
                    layer.Moves.Add(new ToolpathMove(new Vector3(lastPos.X, lastPos.Y, z), p, MoveKind.Travel));
            }
            else
            {
                layer.Moves.Add(new ToolpathMove(prev, p, MoveKind.Extrude) { Normal = norm });
                printed.Add((new Vector2(prev.X, prev.Y), new Vector2(p.X, p.Y)));
            }

            prev = p;
            count++;
        }

        if (count > 2 && first.HasValue && isClosed)
        {
            layer.Moves.Add(new ToolpathMove(prev, new Vector3(first.Value.X, first.Value.Y, z), MoveKind.Extrude)
                { Normal = firstNorm });
            printed.Add((new Vector2(prev.X, prev.Y), first.Value));
        }

        if (count > 0)
            lastPos = new Vector2(prev.X, prev.Y);
    }

    public static int CountCrossings(Vector2 from, Vector2 to, IReadOnlyList<(Vector2 a, Vector2 b)> printed)
    {
        int count = 0;
        foreach (var (a, b) in printed)
        {
            if (SegmentsIntersect(from, to, a, b))
                count++;
        }
        return count;
    }

    private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
    {
        const float eps = 1e-4f;
        var d1 = p2 - p1;
        var d2 = p4 - p3;
        float den = d1.X * d2.Y - d1.Y * d2.X;
        if (MathF.Abs(den) < eps) return false;

        var d3 = p3 - p1;
        float t = (d3.X * d2.Y - d3.Y * d2.X) / den;
        float u = (d3.X * d1.Y - d3.Y * d1.X) / den;
        return t > eps && t < 1f - eps && u > eps && u < 1f - eps;
    }

    private static float MinDistanceToContour(IReadOnlyList<Vector2> contour, Vector2 p)
    {
        if (contour.Count == 0) return float.MaxValue;
        float best = float.MaxValue;
        int n = contour.Count;
        for (int i = 0; i < n; i++)
        {
            var a = contour[i];
            var b = contour[(i + 1) % n];
            float t = ClosestT(a, b, p);
            var pt = a + t * (b - a);
            best = MathF.Min(best, Dist2(pt, p));
        }
        return MathF.Sqrt(best);
    }

    private static void InsertSeamAndRotate(List<Vector2> contour, int bestEdge, float bestT, ref Vector2 prevSeamXY)
    {
        var pa = contour[bestEdge];
        var pb = contour[(bestEdge + 1) % contour.Count];
        var seamPt = pa + bestT * (pb - pa);

        int insertAt = bestEdge + 1;
        if      (Dist2(seamPt, pa) < 1e-4f) insertAt = bestEdge;
        else if (Dist2(seamPt, pb) < 1e-4f) insertAt = (bestEdge + 1) % contour.Count;
        else contour.Insert(insertAt, seamPt);

        if (insertAt % contour.Count != 0)
            RotateClosedContour(contour, null, insertAt);

        prevSeamXY = contour[0];
    }

    private static void RotateClosedContour(List<Vector2> contour, List<Vector3>? normals, int start)
    {
        if (start <= 0 || start >= contour.Count) return;
        var rotated = new List<Vector2>(contour.Count);
        rotated.AddRange(contour.GetRange(start, contour.Count - start));
        rotated.AddRange(contour.GetRange(0, start));
        contour.Clear();
        contour.AddRange(rotated);

        if (normals is null || normals.Count != rotated.Count) return;
        var rotatedN = new List<Vector3>(normals.Count);
        rotatedN.AddRange(normals.GetRange(start, normals.Count - start));
        rotatedN.AddRange(normals.GetRange(0, start));
        normals.Clear();
        normals.AddRange(rotatedN);
    }

    private static void ReverseContour(List<Vector2> contour, List<Vector3>? normals)
    {
        contour.Reverse();
        normals?.Reverse();
    }

    private static void SeamEdgeFromRay(
        IReadOnlyList<Vector2> contour, Vector2 seamOrigin, Vector2 seamDir,
        out int edge, out float t)
    {
        var rayDir = -seamDir;
        float bestRayT = float.MaxValue;
        edge = 0;
        t = 0f;

        for (int i = 0; i < contour.Count; i++)
        {
            var a = contour[i];
            var b = contour[(i + 1) % contour.Count];
            if (RaySegment(seamOrigin, rayDir, a, b, out float rayT, out float segS) && rayT < bestRayT)
            {
                bestRayT = rayT;
                edge = i;
                t = segS;
            }
        }
    }

    private static bool RaySegment(Vector2 origin, Vector2 dir, Vector2 a, Vector2 b,
        out float t, out float s)
    {
        var ab = b - a;
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
}