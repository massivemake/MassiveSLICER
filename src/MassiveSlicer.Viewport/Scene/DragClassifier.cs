using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Classifies a completed gizmo drag by whether it changed which direction a node's own local
/// Z (up) axis points in world space — the distinction that decides whether a drag-release needs
/// a real re-slice. A plain move or a pure spin around that same axis leaves the up-axis
/// pointing the same way and never changes what a planar slicer would produce (confirmed:
/// PlanarSlicer re-derives its Z bounds from the mesh's own current world-transformed vertices
/// every time, never against a fixed world-Z grid) — only an actual tilt does.
/// </summary>
public static class DragClassifier
{
    /// <summary>True when the node's own up-axis direction differs between the two transforms —
    /// i.e. a real tilt happened, not just a move or a Z-spin.</summary>
    public static bool ChangedUpAxis(Matrix4 before, Matrix4 after)
    {
        var upBefore = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, before));
        var upAfter  = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, after));
        return Vector3.Dot(upBefore, upAfter) < 0.999f;
    }
}
