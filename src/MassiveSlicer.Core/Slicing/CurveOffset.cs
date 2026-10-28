using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Moves a polyline sideways in its own plane. Outside is away from the center of a
/// closed loop, and to the left of travel for an open chain. Flip swaps that side.
/// </summary>
public static class CurveOffset
{
    public static List<Vector3> Apply(
        IReadOnlyList<Vector3> points, bool closed, bool flipped, AdaMachiningSettings settings)
    {
        float stock = MathF.Max(0f, settings.StockToLeaveMm);
        return Shift(points, closed, flipped, settings.CurveSide, settings.CurveSideOffsetMm,
            settings.CurveIncludeToolRadius, settings.ToolDiameterMm, stock);
    }

    public static List<Vector3> Shift(
        IReadOnlyList<Vector3> points, bool closed, bool flipped,
        CurveOffsetSide side, float offsetMm, bool includeToolRadius, float toolDiameterMm, float stockToLeaveMm)
    {
        if (points.Count == 0) return [];
        float signed = SignedDistance(side, offsetMm, includeToolRadius, toolDiameterMm, stockToLeaveMm);
        if (flipped) signed = -signed;
        if (MathF.Abs(signed) < 1e-4f) return [.. points];

        int n = points.Count;
        bool droppedClose = closed && n >= 2 && Vector3.Distance(points[0], points[n - 1]) < 1e-3f;
        if (droppedClose) n--;
        if (n < 2) return [.. points];

        FitPlane(points, n, out var origin, out var normal, out var axisU, out var axisV);
        var uv = new Vector2[n];
        var height = new float[n];
        for (int i = 0; i < n; i++)
        {
            var d = points[i] - origin;
            uv[i] = new Vector2(Vector3.Dot(d, axisU), Vector3.Dot(d, axisV));
            height[i] = Vector3.Dot(d, normal);
        }

        float area = Shoelace(uv);
        bool ring = closed && n >= 3 && MathF.Abs(area) > 1e-4f;
        // Positive area is counter-clockwise, so a left normal points inside.
        float outsideSign = ring && area > 0f ? -1f : 1f;

        var shifted = new List<Vector2>(n + 2);
        var heights = new List<float>(n + 2);
        for (int i = 0; i < n; i++)
        {
            OffsetVertex(uv, i, ring, out var left, out var bevel, out var left2);
            shifted.Add(uv[i] + left * (outsideSign * signed));
            heights.Add(height[i]);
            if (bevel)
            {
                shifted.Add(uv[i] + left2 * (outsideSign * signed));
                heights.Add(height[i]);
            }
        }

        var world = new List<Vector3>(shifted.Count);
        for (int i = 0; i < shifted.Count; i++)
        {
            world.Add(origin + axisU * shifted[i].X + axisV * shifted[i].Y + normal * heights[i]);
        }
        return world;
    }

    /// <summary>Direction depth passes travel. Flat curves step down in Z.</summary>
    public static Vector3 DownAxis(IReadOnlyList<Vector3> points, bool closed)
    {
        int n = points.Count;
        if (closed && n >= 2 && Vector3.Distance(points[0], points[n - 1]) < 1e-3f) n--;
        FitPlane(points, Math.Max(0, n), out _, out var normal, out _, out _);
        if (MathF.Abs(normal.Z) < 0.5f) return -Vector3.UnitZ;
        return normal.Z > 0f ? -normal : normal;
    }

    public static float SignedDistance(
        CurveOffsetSide side, float offsetMm, bool includeToolRadius, float toolDiameterMm, float stockToLeaveMm)
    {
        float mag = MathF.Abs(offsetMm);
        float signed = side switch
        {
            CurveOffsetSide.Inside => -mag,
            CurveOffsetSide.OnLine => 0f,
            _ => mag,
        };
        if (includeToolRadius && side != CurveOffsetSide.OnLine)
        {
            float radius = MathF.Max(0f, toolDiameterMm) * 0.5f;
            signed += side == CurveOffsetSide.Inside ? -radius : radius;
        }
        signed += MathF.Max(0f, stockToLeaveMm);
        return signed;
    }

    static void FitPlane(
        IReadOnlyList<Vector3> points, int n,
        out Vector3 origin, out Vector3 normal, out Vector3 axisU, out Vector3 axisV)
    {
        origin = Vector3.Zero;
        if (n <= 0)
        {
            normal = Vector3.UnitZ;
            axisU = Vector3.UnitX;
            axisV = Vector3.UnitY;
            return;
        }
        for (int i = 0; i < n; i++) origin += points[i];
        origin /= n;

        var newell = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % n];
            newell.X += (a.Y - b.Y) * (a.Z + b.Z);
            newell.Y += (a.Z - b.Z) * (a.X + b.X);
            newell.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        if (newell.LengthSquared() < 1e-8f)
        {
            var dir = Vector3.UnitX;
            for (int i = 1; i < n; i++)
            {
                var d = points[i] - points[0];
                if (d.LengthSquared() > 1e-8f) { dir = Vector3.Normalize(d); break; }
            }
            // A straight line does not define a plane. Keep a flat line in XY so the
            // offset moves sideways on the plate. A vertical line shifts off that axis.
            normal = MathF.Abs(dir.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        }
        else
            normal = Vector3.Normalize(newell);

        var edge = Vector3.UnitX;
        for (int i = 1; i < n; i++)
        {
            var d = points[i] - points[0];
            var on = d - normal * Vector3.Dot(d, normal);
            if (on.LengthSquared() > 1e-8f) { edge = Vector3.Normalize(on); break; }
        }
        axisU = edge;
        axisV = Vector3.Normalize(Vector3.Cross(normal, axisU));
        axisU = Vector3.Normalize(Vector3.Cross(axisV, normal));
    }

    static float Shoelace(Vector2[] uv)
    {
        float area = 0f;
        for (int i = 0; i < uv.Length; i++)
        {
            var a = uv[i];
            var b = uv[(i + 1) % uv.Length];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area * 0.5f;
    }

    static void OffsetVertex(Vector2[] uv, int i, bool ring, out Vector2 left, out bool bevel, out Vector2 left2)
    {
        int n = uv.Length;
        bevel = false;
        left2 = default;
        bool openStart = !ring && i == 0;
        bool openEnd = !ring && i == n - 1;
        Vector2 t1 = Tangent(uv, openStart ? i : i - 1, openStart ? i + 1 : i, n, ring);
        Vector2 t2 = Tangent(uv, openEnd ? i - 1 : i, openEnd ? i : i + 1, n, ring);
        if (t1.LengthSquared() < 1e-12f) t1 = t2;
        if (t2.LengthSquared() < 1e-12f) t2 = t1;
        if (t1.LengthSquared() < 1e-12f) t1 = Vector2.UnitX;
        if (t2.LengthSquared() < 1e-12f) t2 = t1;
        t1 = Vector2.Normalize(t1);
        t2 = Vector2.Normalize(t2);
        var n1 = new Vector2(-t1.Y, t1.X);
        var n2 = new Vector2(-t2.Y, t2.X);
        if (openStart) { left = n2; return; }
        if (openEnd) { left = n1; return; }
        var nn = n1 + n2;
        if (nn.LengthSquared() < 1e-8f)
        {
            bevel = true;
            left = n1;
            left2 = n2;
            return;
        }
        nn = Vector2.Normalize(nn);
        float dot = Vector2.Dot(nn, n1);
        if (dot < 0.25f)
        {
            bevel = true;
            left = n1;
            left2 = n2;
            return;
        }
        left = nn / dot;
    }

    static Vector2 Tangent(Vector2[] uv, int from, int to, int n, bool ring)
    {
        int a = Wrap(from, n, ring);
        int b = Wrap(to, n, ring);
        if (a < 0 || b < 0) return default;
        return uv[b] - uv[a];
    }

    static int Wrap(int i, int n, bool ring)
    {
        if (n <= 0) return -1;
        if (ring) return (i % n + n) % n;
        if (i < 0 || i >= n) return -1;
        return i;
    }
}
