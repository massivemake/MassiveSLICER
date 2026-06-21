using System.Numerics;

namespace MassiveSlicer.Core.Slicing;

/// <summary>Shared helpers for <see cref="Models.SlicingMode.Surface"/> slicing.</summary>
internal static class SurfaceSlicing
{
    /// <summary>
    /// Drops inner skins of thin-walled meshes while keeping real holes.
    /// Removes a nested contour when it sits inside a larger sibling and is closer
    /// than ~1.5× bead width to that parent's boundary.
    /// </summary>
    internal static int[] FilterContours(
        List<List<Vector2>> contours, int[] depths, float beadWidth)
    {
        int n = contours.Count;
        if (n <= 1) return depths;

        var drop = new bool[n];
        float thinTol = MathF.Max(beadWidth * 1.5f, 1f);

        for (int i = 0; i < n; i++)
        {
            if (contours[i].Count < 3) { drop[i] = true; continue; }
            var ci = Centroid(contours[i]);
            float ai = MathF.Abs(SignedArea(contours[i]));

            for (int j = 0; j < n; j++)
            {
                if (i == j || contours[j].Count < 3) continue;
                if (MathF.Abs(SignedArea(contours[j])) <= ai) continue;
                if (!PointInPolygon(ci, contours[j])) continue;

                if (MinDistToPoly(ci, contours[j]) < thinTol)
                {
                    drop[i] = true;
                    break;
                }
            }
        }

        var newContours = new List<List<Vector2>>(n);
        var newDepths   = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (drop[i]) continue;
            newContours.Add(contours[i]);
            newDepths.Add(depths[i]);
        }

        contours.Clear();
        contours.AddRange(newContours);
        return [.. newDepths];
    }

    private static Vector2 Centroid(List<Vector2> poly)
    {
        float sx = 0f, sy = 0f;
        foreach (var p in poly) { sx += p.X; sy += p.Y; }
        float inv = 1f / poly.Count;
        return new Vector2(sx * inv, sy * inv);
    }

    private static float MinDistToPoly(Vector2 p, List<Vector2> poly)
    {
        float best = float.MaxValue;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            float t  = ClosestT(a, b, p);
            var  q   = a + t * (b - a);
            float d2 = Dist2(p, q);
            if (d2 < best) best = d2;
        }
        return MathF.Sqrt(best);
    }

    private static float ClosestT(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        float d = ab.LengthSquared();
        if (d < 1e-10f) return 0f;
        return Math.Clamp(Vector2.Dot(p - a, ab) / d, 0f, 1f);
    }

    private static float Dist2(Vector2 a, Vector2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
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
}