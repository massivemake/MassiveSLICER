using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Locks a preview cylinder + TCP to a named primitive on
/// <c>assets/cells/LFAM3/Toolheads/spindle.glb</c>.
/// Prefer <c>SpindleBitTCP</c> (authoring plane: TCP origin on the plane, +Z = plane
/// normal / purple shop line). Fall back to <c>SpindleBit</c> (legacy disc + housing axis).
/// Spindle GLB verts are baked to <b>metres</b> (flange then applies GltfToScene ×1000).
/// UI lengths stay millimetres — <see cref="MmToParentLocal"/> converts before CreateCylinder.
/// </summary>
public static class SpindleBitCylinder
{
    public const string MaterialToken = "SpindleBit";
    public const string TcpPlaneToken = "SpindleBitTCP";
    public const string NodeName = "__SpindleBitCylinder";

    public static bool IsTcpPlane(SceneNode n)
        => HasToken(n, TcpPlaneToken);

    public static SceneNode? FindTcpPlane(SceneNode toolRoot)
        => FindByToken(toolRoot, TcpPlaneToken);

    public static SceneNode? FindAnchor(SceneNode toolRoot)
        => FindTcpPlane(toolRoot) ?? FindLegacyBit(toolRoot);

    static SceneNode? FindLegacyBit(SceneNode toolRoot)
    {
        SceneNode? named = null;
        foreach (var n in toolRoot.SelfAndDescendants())
        {
            if (IsTcpPlane(n)) continue;
            if (n.Name.Contains(MaterialToken, StringComparison.OrdinalIgnoreCase))
                return n;
            if (named is null &&
                (n.PendingMesh?.Name.Contains(MaterialToken, StringComparison.OrdinalIgnoreCase) == true)
                && n.PendingMesh.Name.Contains(TcpPlaneToken, StringComparison.OrdinalIgnoreCase) == false)
                named = n;
        }
        return named;
    }

    static SceneNode? FindByToken(SceneNode toolRoot, string token)
    {
        SceneNode? named = null;
        foreach (var n in toolRoot.SelfAndDescendants())
        {
            if (n.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                return n;
            if (named is null &&
                (n.PendingMesh?.Name.Contains(token, StringComparison.OrdinalIgnoreCase) == true))
                named = n;
        }
        return named;
    }

    static bool HasToken(SceneNode n, string token)
        => n.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
           || (n.PendingMesh?.Name.Contains(token, StringComparison.OrdinalIgnoreCase) == true)
           || (n.Mesh?.PickingData?.Name.Contains(token, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Datum plane is authoring-only — hide it so the green bit / TCP sit on an
    /// invisible face rather than a visible quad.
    /// </summary>
    public static void HideTcpDatum(SceneNode toolRoot)
    {
        var plane = FindTcpPlane(toolRoot);
        if (plane is null) return;
        plane.Visible = false;
        plane.PickIgnore = true;
        plane.IsAuthoringOverlay = true;
        plane.Selectable = false;
    }

    /// <summary>
    /// Housing = largest non-bit mesh under the tool. Used so the preview follows
    /// the spindle, not the bit puck's thick axis (that puck is ~31 mm along X and
    /// is not coaxial with the housing).
    /// </summary>
    public static SceneNode? FindHousing(SceneNode toolRoot, SceneNode disc)
    {
        SceneNode? best = null;
        float bestVol = -1f;
        foreach (var n in toolRoot.SelfAndDescendants())
        {
            if (ReferenceEquals(n, disc) || n.Name == NodeName || IsTcpPlane(n)) continue;
            var mesh = n.PendingMesh ?? n.Mesh?.PickingData;
            if (mesh is null || mesh.Positions.Length < 8) continue;
            var (min, max) = mesh.LocalBounds;
            var e = max - min;
            float vol = MathF.Abs(e.X * e.Y * e.Z);
            if (vol > bestVol)
            {
                bestVol = vol;
                best = n;
            }
        }
        return best;
    }

    /// <summary>
    /// Disc-local frame: origin at the datum centre. +Z is the <c>SpindleBitTCP</c>
    /// plane normal when that primitive exists (perpendicular to the authored face,
    /// the purple shop line). Otherwise +Z follows the housing long axis (legacy
    /// <c>SpindleBit</c> puck). Always flipped away from the housing body.
    /// </summary>
    public static Matrix4 ComputeLocalTransform(SceneNode toolRoot, SceneNode disc, MeshData discMesh, bool flip)
    {
        var center = MeshCentroid(discMesh);
        var housing = FindHousing(toolRoot, disc);
        Vector3? bodyLocal = null;
        if (housing is not null && (housing.PendingMesh ?? housing.Mesh?.PickingData) is { } hMesh)
            bodyLocal = ToNodeLocal(disc, MeshCentroid(hMesh), housing);

        Vector3 axis;
        if (IsTcpPlane(disc))
        {
            // Authored plane: Z is perpendicular to the face, not the housing AABB.
            axis = EstimatePlaneNormal(discMesh);
        }
        else if (housing is not null && (housing.PendingMesh ?? housing.Mesh?.PickingData) is { } houseMesh)
        {
            axis = DirectionToNodeLocal(disc, EstimateFaceNormal(houseMesh), housing);
            if (axis.LengthSquared < 1e-10f)
                axis = EstimateFaceNormal(discMesh);
        }
        else
        {
            axis = EstimateFaceNormal(discMesh);
        }

        return ComputeLocalTransform(center, axis, bodyLocal, flip);
    }

    /// <summary>
    /// Origin at the disc centroid; +Z along <paramref name="faceNormal"/>,
    /// oriented away from the spindle body.
    /// </summary>
    public static Matrix4 ComputeLocalTransform(
        MeshData disc,
        Vector3? bodyCentroidLocal,
        bool flip)
        => ComputeLocalTransform(
            MeshCentroid(disc),
            EstimateFaceNormal(disc),
            bodyCentroidLocal,
            flip);

    static Vector3 ToNodeLocal(SceneNode dest, Vector3 srcLocalPoint, SceneNode src)
    {
        var world = Vector3.TransformPosition(srcLocalPoint, src.WorldTransform);
        Matrix4.Invert(dest.WorldTransform, out var inv);
        return Vector3.TransformPosition(world, inv);
    }

    static Vector3 DirectionToNodeLocal(SceneNode dest, Vector3 srcLocalDir, SceneNode src)
    {
        var world0 = Vector3.TransformPosition(Vector3.Zero, src.WorldTransform);
        var world1 = Vector3.TransformPosition(srcLocalDir, src.WorldTransform);
        var worldDir = world1 - world0;
        if (worldDir.LengthSquared < 1e-20f) return srcLocalDir;
        Matrix4.Invert(dest.WorldTransform, out var inv);
        return Vector3.TransformVector(worldDir, inv);
    }

    /// <summary>Public for tests: build the lock frame from a known centre + normal.</summary>
    public static Matrix4 ComputeLocalTransform(
        Vector3 center,
        Vector3 faceNormal,
        Vector3? bodyCentroidLocal,
        bool flip)
    {
        var axis = faceNormal.LengthSquared < 1e-10f ? Vector3.UnitZ : Vector3.Normalize(faceNormal);

        if (bodyCentroidLocal is { } body)
        {
            var away = center - body;
            if (away.LengthSquared > 1e-8f && Vector3.Dot(axis, away) < 0f)
                axis = -axis;
        }

        if (flip)
            axis = -axis;

        var z = Vector3.Normalize(axis);
        var hint = MathF.Abs(z.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        var x = Vector3.Normalize(Vector3.Cross(hint, z));
        if (x.LengthSquared < 1e-10f)
            x = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, z));
        var y = Vector3.Cross(z, x);
        return new Matrix4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            center.X, center.Y, center.Z, 1f);
    }

    /// <summary>
    /// Normal of an authored plane (SpindleBitTCP): first usable triangle, then
    /// the thin AABB axis. Does not use housing symmetry.
    /// </summary>
    public static Vector3 EstimatePlaneNormal(MeshData mesh)
    {
        var fromTris = FirstTriangleNormal(mesh);
        if (fromTris.LengthSquared > 1e-10f)
            return Vector3.Normalize(fromTris);

        var (min, max) = mesh.LocalBounds;
        return UniqueExtentAxis(max - min);
    }

    public static Vector3 FirstTriangleNormal(MeshData mesh)
    {
        if (mesh.Positions.Length < 3)
            return Vector3.Zero;

        if (mesh.Indices is { Length: >= 3 } idx)
        {
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                var n = TriangleNormal(
                    mesh.Positions[idx[i]],
                    mesh.Positions[idx[i + 1]],
                    mesh.Positions[idx[i + 2]]);
                if (n.LengthSquared > 1e-12f)
                    return n;
            }
        }
        else
        {
            for (int i = 0; i + 2 < mesh.Positions.Length; i += 3)
            {
                var n = TriangleNormal(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]);
                if (n.LengthSquared > 1e-12f)
                    return n;
            }
        }

        return Vector3.Zero;
    }

    static Vector3 TriangleNormal(Vector3 a, Vector3 b, Vector3 c)
        => Vector3.Cross(b - a, c - a);

    /// <summary>
    /// Disc-face normal = axis of rotational symmetry (points form a circle
    /// in the plane perpendicular to it). Vertex-normal averages are not used:
    /// a thick puck has more rim verts than face verts, which slants the axis.
    /// </summary>
    public static Vector3 EstimateFaceNormal(MeshData mesh)
    {
        var fromCircle = BestRotationalAxis(mesh.Positions);
        if (fromCircle.LengthSquared > 1e-6f)
            return Vector3.Normalize(fromCircle);

        var (min, max) = mesh.LocalBounds;
        var ext = max - min;
        return UniqueExtentAxis(ext);
    }

    /// <summary>
    /// Among candidate axes, pick the one whose projected points are the most
    /// circular (lowest radial coefficient of variation).
    /// </summary>
    public static Vector3 BestRotationalAxis(Vector3[] positions)
    {
        if (positions.Length < 8)
            return Vector3.Zero;

        var center = Vector3.Zero;
        foreach (var p in positions)
            center += p;
        center /= positions.Length;

        var (min, max) = Bounds(positions);
        var ext = max - min;

        Span<Vector3> candidates =
        [
            Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
            UniqueExtentAxis(ext),
        ];

        var bestAxis = Vector3.Zero;
        var bestScore = float.MaxValue;
        foreach (var raw in candidates)
        {
            if (raw.LengthSquared < 1e-10f) continue;
            var axis = Vector3.Normalize(raw);
            float score = RadialSpread(positions, center, axis);
            if (score < bestScore)
            {
                bestScore = score;
                bestAxis = axis;
            }
        }

        // A real disc is nearly circular (CV well below 0.25). Reject junk.
        return bestScore < 0.35f ? bestAxis : Vector3.Zero;
    }

    /// <summary>The AABB axis that is unlike the other two — disc thickness, thin or thick.</summary>
    public static Vector3 UniqueExtentAxis(Vector3 ext)
    {
        float ax = MathF.Abs(ext.X), ay = MathF.Abs(ext.Y), az = MathF.Abs(ext.Z);
        // The pair of matching extents spans the disc; the leftover axis is the normal.
        float yz = MathF.Abs(ay - az); // small ⇒ Y≈Z ⇒ axis X
        float xz = MathF.Abs(ax - az);
        float xy = MathF.Abs(ax - ay);
        if (yz <= xz && yz <= xy) return Vector3.UnitX;
        if (xz <= yz && xz <= xy) return Vector3.UnitY;
        return Vector3.UnitZ;
    }

    static float RadialSpread(Vector3[] pts, Vector3 center, Vector3 axis)
    {
        double sum = 0, sum2 = 0;
        int n = 0;
        foreach (var p in pts)
        {
            var d = p - center;
            var radial = d - axis * Vector3.Dot(d, axis);
            float r = radial.Length;
            sum += r;
            sum2 += r * r;
            n++;
        }
        if (n == 0) return float.MaxValue;
        double mean = sum / n;
        if (mean < 1e-5) return float.MaxValue;
        double var = Math.Max(0, sum2 / n - mean * mean);
        return (float)(Math.Sqrt(var) / mean);
    }

    static (Vector3 Min, Vector3 Max) Bounds(Vector3[] pts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in pts)
        {
            min = Vector3.ComponentMin(min, p);
            max = Vector3.ComponentMax(max, p);
        }
        return (min, max);
    }

    public static Vector3 MeshCentroid(MeshData mesh)
    {
        if (mesh.Positions.Length == 0)
            return Vector3.Zero;
        var sum = Vector3.Zero;
        foreach (var p in mesh.Positions)
            sum += p;
        return sum / mesh.Positions.Length;
    }

    /// <summary>
    /// Convert a millimetre length into the parent disc's local units.
    /// Baked tool meshes are metres (AABB diag &lt; 10). Raw mm CAD is tens–hundreds.
    /// </summary>
    public static float MmToParentLocal(MeshData? parent, float mm)
    {
        if (mm <= 0f) return 0f;
        if (parent is null || parent.Positions.Length == 0)
            return mm * 0.001f;
        var (min, max) = parent.LocalBounds;
        float diag = (max - min).Length;
        return diag > 10f ? mm : mm * 0.001f;
    }

    /// <summary>
    /// World-space cutter: disc centroid, or the preview-cylinder tip when present.
    /// Axis points away from the spindle body (toward the table when the bit hangs down).
    /// </summary>
    public static bool TryGetCutterWorld(SceneNode toolRoot, out Vector3 origin, out Vector3 axisAway)
    {
        origin = default;
        axisAway = default;
        var anchor = FindAnchor(toolRoot);
        if (anchor is null) return false;

        var mesh = anchor.PendingMesh ?? anchor.Mesh?.PickingData;
        if (mesh is null || mesh.Positions.Length < 3) return false;

        var local = ComputeLocalTransform(toolRoot, anchor, mesh, flip: false);
        var world = local * anchor.WorldTransform;
        origin = world.Row3.Xyz;
        axisAway = SafeDir(world.Row2.Xyz);

        foreach (var n in anchor.Children)
        {
            if (n.Name != NodeName) continue;
            var cw = n.WorldTransform;
            var cyl = n.PendingMesh ?? n.Mesh?.PickingData;
            float h = 0f;
            if (cyl is not null)
            {
                var (_, max) = cyl.LocalBounds;
                h = max.Z;
            }
            origin = Vector3.TransformPosition(new Vector3(0f, 0f, h), cw);
            axisAway = SafeDir(cw.Row2.Xyz);
            break;
        }

        return axisAway.LengthSquared > 1e-10f;
    }

    static Vector3 SafeDir(Vector3 v)
        => v.LengthSquared < 1e-10f ? Vector3.UnitZ : Vector3.Normalize(v);

    public static SceneNode BuildNode(float diameterMm, float lengthMm, Matrix4 local, MeshData? parentMesh = null)
    {
        float scale = parentMesh is null
            ? 0.001f
            : MmToParentLocal(parentMesh, 1f);
        var mesh = MeshFactory.CreateCylinder(
            radius: Math.Max(diameterMm, 0.2f) * 0.5f * scale,
            height: Math.Max(lengthMm, 0.2f) * scale,
            name: NodeName);
        return new SceneNode
        {
            Name = NodeName,
            PendingMesh = mesh,
            LocalTransform = local,
            Selectable = false,
            PickIgnore = true,
            KeepOwnMaterial = true,
            CullFaces = true,
        };
    }

    /// <summary>
    /// Parent the preview to the tool root (not the hidden SpindleBitTCP plane —
    /// Visible=false on the datum would swallow the cylinder).
    /// <paramref name="local"/> from <see cref="ComputeLocalTransform"/> is in
    /// <paramref name="anchor"/> space and is converted onto the tool.
    /// </summary>
    public static void AttachPreview(SceneNode tool, SceneNode anchor, SceneNode cyl)
    {
        var desiredWorld = cyl.LocalTransform * anchor.WorldTransform;
        Matrix4.Invert(tool.WorldTransform, out var invTool);
        var localOnTool = desiredWorld * invTool;

        foreach (var n in tool.SelfAndDescendants().ToList())
        {
            if (n.Name == NodeName && !ReferenceEquals(n, cyl))
                n.Parent?.RemoveChild(n);
        }

        cyl.Parent?.RemoveChild(cyl);
        cyl.LocalTransform = localOnTool;
        tool.AddChild(cyl);
    }
}
