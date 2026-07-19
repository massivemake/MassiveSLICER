using System.Numerics;

namespace MassiveSlicer.Core.Collision;

/// <summary>
/// A convex hull posed in world space: vertices pre-transformed once per pose,
/// then support queries are brute-force max-dot (hulls are ≤ ~256 verts).
/// </summary>
public sealed class TransformedHull : ISupport
{
    readonly Vector3[] _worldVerts;
    public Aabb Bounds { get; }

    public TransformedHull(ConvexHull hull, in Matrix4x4 worldTransform)
    {
        _worldVerts = new Vector3[hull.Vertices.Length];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < _worldVerts.Length; i++)
        {
            var p = Vector3.Transform(hull.Vertices[i], worldTransform);
            _worldVerts[i] = p;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        Bounds = new Aabb(min, max);
    }

    public Vector3 Support(Vector3 dir)
    {
        var best = _worldVerts[0];
        float bestDot = Vector3.Dot(best, dir);
        for (int i = 1; i < _worldVerts.Length; i++)
        {
            float d = Vector3.Dot(_worldVerts[i], dir);
            if (d > bestDot) { bestDot = d; best = _worldVerts[i]; }
        }
        return best;
    }
}

/// <summary>A single world-space triangle (environment mesh leaf).</summary>
public readonly struct TriangleSupport : ISupport
{
    readonly Vector3 _a, _b, _c;
    public Aabb Bounds { get; }

    public TriangleSupport(Vector3 a, Vector3 b, Vector3 c)
    {
        _a = a; _b = b; _c = c;
        Bounds = new Aabb(
            Vector3.Min(a, Vector3.Min(b, c)),
            Vector3.Max(a, Vector3.Max(b, c)));
    }

    public Vector3 Support(Vector3 dir)
    {
        float da = Vector3.Dot(_a, dir);
        float db = Vector3.Dot(_b, dir);
        float dc = Vector3.Dot(_c, dir);
        return da >= db
            ? (da >= dc ? _a : _c)
            : (db >= dc ? _b : _c);
    }
}

/// <summary>
/// Oriented box (deposited bead segment): center + three orthonormal axes with
/// half-extents. Support is analytic — no vertex list needed.
/// </summary>
public readonly struct ObbSupport : ISupport
{
    readonly Vector3 _center;
    readonly Vector3 _ax0, _ax1, _ax2;   // unit axes
    readonly Vector3 _half;              // half extents along ax0/ax1/ax2
    public Aabb Bounds { get; }

    public ObbSupport(Vector3 center, Vector3 ax0, Vector3 ax1, Vector3 ax2, Vector3 halfExtents)
    {
        _center = center;
        _ax0 = ax0; _ax1 = ax1; _ax2 = ax2;
        _half = halfExtents;

        // AABB = center ± sum(|axis_i| * half_i) componentwise.
        var r = new Vector3(
            MathF.Abs(ax0.X) * halfExtents.X + MathF.Abs(ax1.X) * halfExtents.Y + MathF.Abs(ax2.X) * halfExtents.Z,
            MathF.Abs(ax0.Y) * halfExtents.X + MathF.Abs(ax1.Y) * halfExtents.Y + MathF.Abs(ax2.Y) * halfExtents.Z,
            MathF.Abs(ax0.Z) * halfExtents.X + MathF.Abs(ax1.Z) * halfExtents.Y + MathF.Abs(ax2.Z) * halfExtents.Z);
        Bounds = new Aabb(center - r, center + r);
    }

    public Vector3 Support(Vector3 dir)
    {
        return _center
            + _ax0 * (Vector3.Dot(dir, _ax0) >= 0f ? _half.X : -_half.X)
            + _ax1 * (Vector3.Dot(dir, _ax1) >= 0f ? _half.Y : -_half.Y)
            + _ax2 * (Vector3.Dot(dir, _ax2) >= 0f ? _half.Z : -_half.Z);
    }
}
