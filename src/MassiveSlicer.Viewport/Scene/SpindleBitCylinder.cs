using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Locks a preview cylinder to the spindle GLB primitive whose material is
/// <c>SpindleBit</c> (see <c>assets/cells/LFAM3/Toolheads/spindle.glb</c>).
/// Mesh local units are millimetres (the node above applies the 0.001 scale).
/// </summary>
public static class SpindleBitCylinder
{
    public const string MaterialToken = "SpindleBit";
    public const string NodeName = "__SpindleBitCylinder";

    public static SceneNode? FindAnchor(SceneNode toolRoot)
    {
        SceneNode? named = null;
        foreach (var n in toolRoot.SelfAndDescendants())
        {
            if (n.Name.Contains(MaterialToken, StringComparison.OrdinalIgnoreCase))
                return n;
            if (named is null &&
                (n.PendingMesh?.Name.Contains(MaterialToken, StringComparison.OrdinalIgnoreCase) == true))
                named = n;
        }
        return named;
    }

    /// <summary>
    /// Origin at the disc centroid; +Z along the disc plane's true normal
    /// (axis of rotational symmetry), oriented away from the spindle body.
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

    public static SceneNode BuildNode(float diameterMm, float lengthMm, Matrix4 local)
    {
        var mesh = MeshFactory.CreateCylinder(
            radius: Math.Max(diameterMm, 0.2f) * 0.5f,
            height: Math.Max(lengthMm, 0.2f),
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
}
