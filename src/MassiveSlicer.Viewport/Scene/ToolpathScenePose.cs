using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Toolpaths are drawn with <see cref="SceneNode.LocalTransform"/> × view-proj —
/// the parent chain is ignored (unlike meshes). Never parent a toolpath under the
/// rotary bed: <c>baseAbc</c> C≈−90 would show up as a tipped path, and switching
/// BASE back compounds that into a pose that is no longer on either plate.
/// </summary>
public static class ToolpathScenePose
{
    /// <summary>
    /// Slide the draw origin by <paramref name="delta"/> (print-surface analogue)
    /// and strip rotation so a leftover rotary inverse cannot persist.
    /// </summary>
    public static Matrix4 ShiftDrawLocal(Matrix4 local, Vector3 delta)
    {
        var t = local.Row3.Xyz + delta;
        return Matrix4.CreateTranslation(t.X, t.Y, t.Z);
    }

    /// <summary>Translation-only frame at an affine pose's origin (row-vector convention).</summary>
    public static Matrix4 TranslationOnly(Matrix4 world)
    {
        var t = Vector3.TransformPosition(Vector3.Zero, world);
        return Matrix4.CreateTranslation(t.X, t.Y, t.Z);
    }
}
