using System.Numerics;

namespace MassiveSlicer.Core.Collision;

/// <summary>Axis-aligned bounding box (world or local space; caller's convention).</summary>
public readonly struct Aabb
{
    public readonly Vector3 Min;
    public readonly Vector3 Max;

    public Aabb(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public static readonly Aabb Empty = new(
        new Vector3(float.MaxValue), new Vector3(float.MinValue));

    public bool IsEmpty => Min.X > Max.X;

    public Vector3 Center => (Min + Max) * 0.5f;
    public Vector3 Extent => Max - Min;

    public static Aabb FromPoints(ReadOnlySpan<Vector3> pts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in pts)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return new Aabb(min, max);
    }

    public Aabb Union(in Aabb other) => new(
        Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    public Aabb Union(Vector3 p) => new(
        Vector3.Min(Min, p), Vector3.Max(Max, p));

    public bool Overlaps(in Aabb other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    public bool Contains(Vector3 p) =>
        p.X >= Min.X && p.X <= Max.X &&
        p.Y >= Min.Y && p.Y <= Max.Y &&
        p.Z >= Min.Z && p.Z <= Max.Z;

    /// <summary>Grows the box by <paramref name="mm"/> on every side.</summary>
    public Aabb Inflate(float mm) => new(
        Min - new Vector3(mm), Max + new Vector3(mm));

    /// <summary>AABB of this box's 8 corners transformed by <paramref name="m"/>.</summary>
    public Aabb Transform(in Matrix4x4 m)
    {
        var result = Empty;
        Span<Vector3> corners =
        [
            new(Min.X, Min.Y, Min.Z), new(Max.X, Min.Y, Min.Z),
            new(Min.X, Max.Y, Min.Z), new(Max.X, Max.Y, Min.Z),
            new(Min.X, Min.Y, Max.Z), new(Max.X, Min.Y, Max.Z),
            new(Min.X, Max.Y, Max.Z), new(Max.X, Max.Y, Max.Z),
        ];
        foreach (var c in corners)
            result = result.Union(Vector3.Transform(c, m));
        return result;
    }

    /// <summary>Surface area (for BVH build heuristics / debugging).</summary>
    public float SurfaceArea()
    {
        if (IsEmpty) return 0f;
        var e = Extent;
        return 2f * (e.X * e.Y + e.Y * e.Z + e.Z * e.X);
    }

    public override string ToString() => $"Aabb[{Min} .. {Max}]";
}
