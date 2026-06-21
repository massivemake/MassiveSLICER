using System.Numerics;

namespace MassiveSlicer.Core.Models;

/// <summary>World-space seam position guide placed by the user in the viewport.</summary>
public sealed record SeamGuidePoint(float X, float Y, float Z)
{
    public Vector3 ToVector3() => new(X, Y, Z);
    public Vector2 ToXY() => new(X, Y);

    public static SeamGuidePoint FromVector3(Vector3 v) => new(v.X, v.Y, v.Z);
}