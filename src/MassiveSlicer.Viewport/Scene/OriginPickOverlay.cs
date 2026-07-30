using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// The translucent bounding box and snap markers shown while Move Origin mode is active.
/// </summary>
/// <remarks>
/// Built in the target's own space and parented to it, so the box tracks the part as it is moved
/// and turned without anything having to re-place it per frame — and because that space is where
/// <see cref="NodeTransform.Origin"/> lives, a marker's position IS the pivot value it sets.
/// <para>
/// The tool moves to the mesh, never the mesh to the tool: nothing here touches the part's transform.
/// </para>
/// </remarks>
public static class OriginPickOverlay
{
    /// <summary>Marks the overlay subtree so it can be found and torn down again.</summary>
    public const string NodeName = "__origin_pick";

    /// <summary>Deliberately fainter than a cut modifier's plane — this is a transient chooser
    /// sitting over the part being inspected, not a modifier the user is positioning.</summary>
    private static readonly Vector4 BoxColor    = new(0.35f, 0.65f, 1.00f, 0.10f);
    private static readonly Vector4 MarkerColor = new(0.55f, 0.80f, 1.00f, 0.95f);

    /// <summary>Marker cube edge, as a fraction of the box's diagonal.</summary>
    private const float MarkerScale = 0.018f;

    /// <summary>
    /// Builds the overlay for <paramref name="box"/> (the target's local-space bounds) and reports
    /// the snap points in the same order as the marker children, so a screen-space pick maps
    /// straight back to a pivot position.
    /// </summary>
    public static SceneNode Build((Vector3 Min, Vector3 Max) box, out Vector3[] snapPoints)
    {
        snapPoints = NodeBounds.SnapPoints(box).ToArray();

        var root = new SceneNode
        {
            Name               = NodeName,
            IsAuthoringOverlay = true,
            PickIgnore         = true,
            Selectable         = false,
            KeepOwnMaterial    = true,
            CullFaces          = false,
        };

        root.AddChild(new SceneNode
        {
            Name               = NodeName + "_box",
            PendingMesh        = BoxMesh(box.Min, box.Max, BoxColor),
            IsAuthoringOverlay = true,
            PickIgnore         = true,
            Selectable         = false,
            KeepOwnMaterial    = true,
            CullFaces          = false,
            TranslucentPass    = true,
        });

        float diag = (box.Max - box.Min).Length;
        float half = MathF.Max(diag * MarkerScale, 1e-3f) * 0.5f;

        foreach (var p in snapPoints)
        {
            root.AddChild(new SceneNode
            {
                Name               = NodeName + "_pt",
                PendingMesh        = BoxMesh(p - new Vector3(half), p + new Vector3(half), MarkerColor),
                IsAuthoringOverlay = true,
                PickIgnore         = true,
                Selectable         = false,
                KeepOwnMaterial    = true,
                CullFaces          = false,
                // Markers must stay visible through the part they sit on, or the ones on the far
                // side — exactly the ones a user reaches for — would be invisible.
                AlwaysOnTop        = true,
            });
        }

        return root;
    }

    private static MeshData BoxMesh(Vector3 min, Vector3 max, Vector4 color)
    {
        var positions = new List<Vector3>(36);
        var normals   = new List<Vector3>(36);

        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            positions.Add(a); positions.Add(b); positions.Add(c);
            positions.Add(a); positions.Add(c); positions.Add(d);
            for (int i = 0; i < 6; i++) normals.Add(n);
        }

        var p000 = new Vector3(min.X, min.Y, min.Z);
        var p100 = new Vector3(max.X, min.Y, min.Z);
        var p110 = new Vector3(max.X, max.Y, min.Z);
        var p010 = new Vector3(min.X, max.Y, min.Z);
        var p001 = new Vector3(min.X, min.Y, max.Z);
        var p101 = new Vector3(max.X, min.Y, max.Z);
        var p111 = new Vector3(max.X, max.Y, max.Z);
        var p011 = new Vector3(min.X, max.Y, max.Z);

        Face(p000, p010, p110, p100, -Vector3.UnitZ);
        Face(p001, p101, p111, p011,  Vector3.UnitZ);
        Face(p000, p100, p101, p001, -Vector3.UnitY);
        Face(p010, p011, p111, p110,  Vector3.UnitY);
        Face(p000, p001, p011, p010, -Vector3.UnitX);
        Face(p100, p110, p111, p101,  Vector3.UnitX);

        return new MeshData(
            positions.ToArray(), normals.ToArray(), indices: null,
            name: NodeName, baseColor: color, metallic: 0f, roughness: 1f);
    }
}
