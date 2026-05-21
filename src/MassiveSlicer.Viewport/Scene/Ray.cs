using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>A world-space ray defined by an origin and normalised direction.</summary>
public readonly struct Ray
{
    public Vector3 Origin    { get; }
    public Vector3 Direction { get; }

    public Ray(Vector3 origin, Vector3 direction)
    {
        Origin    = origin;
        Direction = direction;
    }

    public Vector3 At(float t) => Origin + Direction * t;
}
