using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>Which side(s) of an open path receive offset copies.</summary>
public enum PathOffsetSide
{
    Both,
    Left,
    Right,
}

/// <summary>
/// Parallel offset of toolpath polylines in a layer plane (AiBuild-style Offset path).
/// Open paths use a vertex-normal offset; closed loops use Clipper inflation.
/// </summary>
public static class PathOffsetter
{
    /// <summary>
    /// Offsets a span of extrude moves, producing one or more parallel polylines
    /// (each polyline is a list of world-space vertices).
    /// </summary>
    /// <param name="distanceMm">Signed distance for the first offset level (negative = opposite side).</param>
    /// <param name="count">How many offset levels to generate (≥1).</param>
    /// <param name="side">Open-path sides; closed paths always use both sides of signed distance.</param>
    public static List<List<Vector3>> OffsetSpan(
        ToolpathLayer layer,
        ContourSpan span,
        float distanceMm,
        int count,
        PathOffsetSide side)
    {
        var result = new List<List<Vector3>>();
        if (count < 1 || MathF.Abs(distanceMm) < 1e-4f) return result;

        var pts = CollectExtrudePoints(layer, span);
        if (pts.Count < 2) return result;

        bool closed = span.Closed
            || Vector3.DistanceSquared(pts[0], pts[^1]) < 1.0f; // ~1 mm²
        var n = layer.PlaneNormal;
        if (n.LengthSquared() < 1e-8f) n = Vector3.UnitZ;
        n = Vector3.Normalize(n);

        var distances = BuildDistances(distanceMm, count, side, closed);
        foreach (float d in distances)
        {
            var off = closed
                ? OffsetClosed(pts, n, d)
                : OffsetOpen(pts, n, d);
            if (off is { Count: >= 2 })
                result.Add(off);
        }
        return result;
    }

    private static List<float> BuildDistances(
        float distanceMm, int count, PathOffsetSide side, bool closed)
    {
        var list = new List<float>();
        for (int i = 1; i <= count; i++)
        {
            float d = distanceMm * i;
            if (closed)
            {
                list.Add(d);
                continue;
            }
            switch (side)
            {
                case PathOffsetSide.Left:
                    list.Add(MathF.Abs(d));
                    break;
                case PathOffsetSide.Right:
                    list.Add(-MathF.Abs(d));
                    break;
                default: // Both
                    list.Add(MathF.Abs(d));
                    list.Add(-MathF.Abs(d));
                    break;
            }
        }
        return list;
    }

    private static List<Vector3> CollectExtrudePoints(ToolpathLayer layer, ContourSpan span)
    {
        var pts = new List<Vector3>();
        int end = Math.Min(layer.Moves.Count, span.Start + Math.Max(0, span.Count));
        for (int i = span.Start; i < end; i++)
        {
            var mv = layer.Moves[i];
            if (mv.Kind != MoveKind.Extrude) continue;
            if (pts.Count == 0)
                pts.Add(mv.From);
            // Always append To; collapse micro-duplicates.
            if (pts.Count == 0 || Vector3.DistanceSquared(pts[^1], mv.To) > 1e-6f)
                pts.Add(mv.To);
        }
        return pts;
    }

    /// <summary>Open polyline offset in the plane perpendicular to <paramref name="n"/>.</summary>
    public static List<Vector3> OffsetOpen(IReadOnlyList<Vector3> pts, Vector3 n, float distance)
    {
        int m = pts.Count;
        if (m < 2) return [];
        var outPts = new List<Vector3>(m);
        for (int i = 0; i < m; i++)
        {
            Vector3 dirIn  = i > 0     ? pts[i] - pts[i - 1] : pts[1] - pts[0];
            Vector3 dirOut = i < m - 1 ? pts[i + 1] - pts[i] : pts[m - 1] - pts[m - 2];
            var leftIn  = LeftNormal(dirIn, n);
            var leftOut = LeftNormal(dirOut, n);
            Vector3 offsetDir;
            if (leftIn.LengthSquared() < 1e-12f && leftOut.LengthSquared() < 1e-12f)
                offsetDir = Vector3.Zero;
            else if (leftIn.LengthSquared() < 1e-12f)
                offsetDir = Vector3.Normalize(leftOut);
            else if (leftOut.LengthSquared() < 1e-12f)
                offsetDir = Vector3.Normalize(leftIn);
            else
            {
                // Miter join: average of unit left-normals, scaled to keep distance.
                var a = Vector3.Normalize(leftIn);
                var b = Vector3.Normalize(leftOut);
                var mid = a + b;
                if (mid.LengthSquared() < 1e-10f)
                    offsetDir = a;
                else
                {
                    mid = Vector3.Normalize(mid);
                    // Miter length limit (~4×) to avoid spikes at sharp corners.
                    float cosHalf = Vector3.Dot(a, mid);
                    float scale = cosHalf > 0.25f ? 1f / cosHalf : 4f;
                    offsetDir = mid * MathF.Min(scale, 4f);
                }
            }
            outPts.Add(pts[i] + offsetDir * distance);
        }
        return outPts;
    }

    /// <summary>Closed loop: offset vertices with miter in-plane (no Clipper dependency for centerlines).</summary>
    public static List<Vector3> OffsetClosed(IReadOnlyList<Vector3> pts, Vector3 n, float distance)
    {
        // Drop duplicate closing point if present.
        int m = pts.Count;
        if (m >= 2 && Vector3.DistanceSquared(pts[0], pts[^1]) < 1.0f)
            m--;
        if (m < 3) return OffsetOpen(pts, n, distance);

        var outPts = new List<Vector3>(m + 1);
        for (int i = 0; i < m; i++)
        {
            int prev = (i + m - 1) % m;
            int next = (i + 1) % m;
            var dirIn  = pts[i] - pts[prev];
            var dirOut = pts[next] - pts[i];
            var a = LeftNormal(dirIn, n);
            var b = LeftNormal(dirOut, n);
            if (a.LengthSquared() > 1e-12f) a = Vector3.Normalize(a);
            if (b.LengthSquared() > 1e-12f) b = Vector3.Normalize(b);
            var mid = a + b;
            Vector3 offsetDir;
            if (mid.LengthSquared() < 1e-10f)
                offsetDir = a.LengthSquared() > 1e-12f ? a : b;
            else
            {
                mid = Vector3.Normalize(mid);
                float cosHalf = Vector3.Dot(a.LengthSquared() > 1e-12f ? a : mid, mid);
                float scale = cosHalf > 0.25f ? 1f / cosHalf : 4f;
                offsetDir = mid * MathF.Min(scale, 4f);
            }
            outPts.Add(pts[i] + offsetDir * distance);
        }
        // Close the loop.
        outPts.Add(outPts[0]);
        return outPts;
    }

    /// <summary>In-plane left normal of direction <paramref name="dir"/> (cross with plane normal).</summary>
    private static Vector3 LeftNormal(Vector3 dir, Vector3 planeN)
    {
        // Project dir into plane then rotate 90° via n × dir.
        var d = dir - planeN * Vector3.Dot(dir, planeN);
        if (d.LengthSquared() < 1e-12f) return Vector3.Zero;
        return Vector3.Cross(planeN, d);
    }

    /// <summary>
    /// Builds extrude moves along an offset polyline, copying print-speed scale from
    /// a template move when available.
    /// </summary>
    public static List<ToolpathMove> PolylineToExtrudes(
        IReadOnlyList<Vector3> pts, ToolpathMove? template = null)
    {
        var moves = new List<ToolpathMove>();
        for (int i = 1; i < pts.Count; i++)
        {
            if (Vector3.DistanceSquared(pts[i - 1], pts[i]) < 1e-8f) continue;
            var mv = new ToolpathMove(pts[i - 1], pts[i], MoveKind.Extrude)
            {
                Normal = template?.Normal ?? Vector3.Zero,
                HeightScale = template?.HeightScale ?? 1f,
                PrintSpeedScale = template?.PrintSpeedScale ?? 1f,
                IsLightning = template?.IsLightning ?? false,
            };
            moves.Add(mv);
        }
        return moves;
    }
}
