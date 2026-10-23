using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// One world-space change applied to every selected object, so they move as one group.
/// Row-vector convention: <c>newWorld = initialWorld * op</c>, then
/// <c>local = newWorld * inverse(parentWorld)</c>.
/// </summary>
public static class GroupTransform
{
    public static Matrix4 Translation(Vector3 worldDelta)
        => Matrix4.CreateTranslation(worldDelta);

    /// <summary>Rotate or scale about <paramref name="pivot"/> in world space.</summary>
    public static Matrix4 AboutPivot(Vector3 pivot, Matrix4 change)
        => Matrix4.CreateTranslation(-pivot) * change * Matrix4.CreateTranslation(pivot);

    public static Matrix4 LocalFromWorld(Matrix4 initialWorld, Matrix4 worldOp, Matrix4 invParent)
        => initialWorld * worldOp * invParent;
}
