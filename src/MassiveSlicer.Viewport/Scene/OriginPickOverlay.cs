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

    /// <summary>
    /// The box centre gets its own colour because it is the one point that is not on the surface,
    /// and the one people reach for most. Gold rather than green: the scene is already full of
    /// greens (bed, toolpath, status) and a green dot would read as one of them.
    /// </summary>
    private static readonly Vector4 CenterColor = new(1.00f, 0.76f, 0.16f, 1.00f);

    /// <summary>Marker cube edge, as a fraction of the box's diagonal. The centre marker is the
    /// same size as the other 26 — Jeff's call, 2026-08-03: the colour alone carries it.</summary>
    private const float MarkerScale = 0.018f;

    /// <summary>
    /// Builds the overlay for <paramref name="box"/> (the target's local-space bounds) and reports
    /// every snap point it drew, so a screen-space pick maps straight back to a pivot position.
    /// </summary>
    /// <remarks>
    /// 27 points: the 26 surface points from <see cref="NodeBounds.SnapPoints"/> plus the box
    /// centre. The centre is reported <em>first</em> but drawn <em>last</em>, and the two orders
    /// disagree on purpose. Looking straight down an axis stacks the centre and both face centres
    /// on the same pixel, and no rule can separate them; drawing last keeps the gold square
    /// unobscured, and picking first means the marker you can see is the one you get. Orbit
    /// slightly to reach the face centre hiding behind it.
    /// </remarks>
    /// <param name="targetScale">
    /// The part's own scale, so the markers can be kept cubic. Everything here is built in the
    /// part's local space and parented to it, which means the part's scale multiplies it on the way
    /// to the screen — fine for the box, which is meant to hug the part, but it turned the marker
    /// cubes into stretched slabs on anything scaled unevenly, and made them balloon or shrink with
    /// the part's overall size. Dividing the marker extents through by the scale first cancels that
    /// back out. Omit for an unscaled part.
    /// </param>
    public static SceneNode Build(
        (Vector3 Min, Vector3 Max) box, out Vector3[] snapPoints, Vector3? targetScale = null)
    {
        var center = NodeBounds.Center(box);
        snapPoints = new[] { center }.Concat(NodeBounds.SnapPoints(box)).ToArray();

        var s = targetScale ?? Vector3.One;
        var absScale = new Vector3(
            MathF.Max(MathF.Abs(s.X), 1e-6f),
            MathF.Max(MathF.Abs(s.Y), 1e-6f),
            MathF.Max(MathF.Abs(s.Z), 1e-6f));

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

        // Sized against the box as it actually appears on screen, not its unscaled local extent, so
        // a part scaled to 300% gets markers that still read as small handles rather than crates.
        var local = box.Max - box.Min;
        float worldDiag = new Vector3(
            local.X * absScale.X, local.Y * absScale.Y, local.Z * absScale.Z).Length;

        // Pre-divided per axis: the part's scale multiplies these back up to a cube on screen.
        float halfWorld = MathF.Max(worldDiag * MarkerScale, 1e-3f) * 0.5f;
        var half = new Vector3(
            halfWorld / absScale.X, halfWorld / absScale.Y, halfWorld / absScale.Z);

        void Marker(Vector3 p, Vector4 color) => root.AddChild(new SceneNode
        {
            Name               = NodeName + "_pt",
            PendingMesh        = BoxMesh(p - half, p + half, color),
            IsAuthoringOverlay = true,
            PickIgnore         = true,
            Selectable         = false,
            KeepOwnMaterial    = true,
            CullFaces          = false,
            // Markers must stay visible through the part they sit on, or the ones on the far
            // side — exactly the ones a user reaches for — would be invisible. The centre marker
            // needs this even more: it is inside the mesh by definition.
            AlwaysOnTop        = true,
        });

        foreach (var p in NodeBounds.SnapPoints(box))
            Marker(p, MarkerColor);

        // Last, so painter's order leaves the gold square whole where a face centre overlaps it —
        // which matters more now that it is no longer bigger than the marker it can hide behind.
        Marker(center, CenterColor);

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
