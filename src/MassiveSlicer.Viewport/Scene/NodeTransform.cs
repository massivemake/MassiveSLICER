using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// A node's placement, stored as separate position / rotation / scale / pivot values instead of
/// one blended <see cref="Matrix4"/>.
/// </summary>
/// <remarks>
/// <para>
/// The old representation kept only the composed matrix and edited it in place, which cost the app
/// three separate things. The pivot had nowhere to live except the matrix's translation column, so
/// it was always whatever the exporting package happened to leave behind. Orientation had to be
/// re-derived from the matrix on every readout, and the derivation divided all three basis rows by
/// the length of the <em>first</em> one — correct only while scale was uniform, garbage the moment
/// it was not. And a local-frame rotation could only be faked by saving the translation column,
/// multiplying, and putting the column back, which is why "rotate about the object's own axis"
/// never quite existed.
/// </para>
/// <para>
/// Keeping the four parts separate removes all three. Scale is applied in the object's own
/// unrotated frame, so the composed basis rows are <c>scale[i] * rotationRow[i]</c> —
/// independently lengthened but still mutually perpendicular. Decomposition is therefore exact:
/// row lengths are the scale, the normalised rows are the rotation. Degrees come from a stored
/// orientation rather than being reverse-engineered.
/// </para>
/// <para>
/// <see cref="Origin"/> is the pivot, expressed in the mesh's own coordinate space — where the
/// file's exporter left its vertices. Rotation and scale both happen about it, and
/// <see cref="Position"/> is where that pivot sits in the parent's space. Setting
/// <c>Position = Origin</c> on a fresh import therefore composes to the identity, which is how the
/// pivot can be re-centred without the geometry appearing to move.
/// </para>
/// <para>
/// Composition order is row-vector (left-most applied first), matching
/// <see cref="SceneNode.WorldTransform"/>'s <c>local * parent</c>.
/// </para>
/// </remarks>
public struct NodeTransform
{
    /// <summary>Where the pivot sits in the parent's space.</summary>
    public Vector3 Position;

    /// <summary>Orientation. Convention-free — see <see cref="EulerDegrees"/> for the UI view of it.</summary>
    public Quaternion Rotation;

    /// <summary>Per-axis scale, applied in the object's own unrotated frame.</summary>
    public Vector3 Scale;

    /// <summary>The pivot, in the mesh's own coordinate space (the origin the file shipped with).</summary>
    public Vector3 Origin;

    public NodeTransform(Vector3 position, Quaternion rotation, Vector3 scale, Vector3 origin)
    {
        Position = position;
        Rotation = rotation;
        Scale    = scale;
        Origin   = origin;
    }

    /// <summary>No movement, no rotation, unit scale, pivot at the mesh's own zero.</summary>
    public static NodeTransform Identity
        => new(Vector3.Zero, Quaternion.Identity, Vector3.One, Vector3.Zero);

    /// <summary>
    /// A transform that leaves the mesh exactly where its file put it, but pivots about
    /// <paramref name="origin"/> instead of the file's zero. This is the one-time re-centring
    /// applied at import and to every piece a cut creates.
    /// </summary>
    public static NodeTransform PivotedAt(Vector3 origin)
        => new(origin, Quaternion.Identity, Vector3.One, origin);

    // -- Composition -----------------------------------------------------------

    /// <summary>Composes to the matrix the renderer and the rest of the app consume.</summary>
    public Matrix4 ToMatrix()
        => Matrix4.CreateTranslation(-Origin)
         * Matrix4.CreateScale(Scale)
         * Matrix4.CreateFromQuaternion(Rotation)
         * Matrix4.CreateTranslation(Position);

    /// <summary>
    /// Recovers the separated values from a composed matrix, keeping <paramref name="origin"/> as
    /// the pivot. Used to carry pre-existing <c>.mass</c> files forward.
    /// </summary>
    /// <remarks>
    /// A matrix saved by an older build may be sheared (see the type remarks). Shear cannot be
    /// represented here, so it is dropped — the basis is squared back up via Gram-Schmidt, which
    /// visibly straightens a part that had been skewed. <see cref="ShearOf"/> measures how far a
    /// matrix is from square so a caller can warn instead of silently changing geometry.
    /// </remarks>
    public static NodeTransform FromMatrix(Matrix4 m, Vector3 origin)
    {
        var r0 = m.Row0.Xyz;
        var r1 = m.Row1.Xyz;
        var r2 = m.Row2.Xyz;

        float sx = r0.Length;
        float sy = r1.Length;
        float sz = r2.Length;

        // Gram-Schmidt: keep X, square Y against it, then take Z as the cross product so the
        // result is guaranteed orthonormal rather than merely close to it.
        var ax = sx > 1e-6f ? r0 / sx : Vector3.UnitX;
        var ay = r1 - Vector3.Dot(r1, ax) * ax;
        ay = ay.LengthSquared > 1e-12f ? Vector3.Normalize(ay) : Orthogonal(ax);
        var az = Vector3.Cross(ax, ay);

        // A negative determinant means the matrix mirrors. A quaternion cannot mirror, so fold the
        // flip into the Z scale and keep the rotation right-handed.
        if (Vector3.Dot(az, r2) < 0f) sz = -sz;

        var rotation = QuatFromRowBasis(new Matrix3(ax, ay, az));
        var scale    = new Vector3(sx, sy, MathF.Abs(sz) < 1e-6f ? 1e-6f : sz);

        // Position is where the pivot ended up: run the pivot point through the original matrix.
        var position = Vector3.TransformPosition(origin, m);

        return new NodeTransform(position, rotation, scale, origin);
    }

    /// <summary>
    /// How far a matrix's basis is from square, as the largest absolute dot product between its
    /// normalised rows (0 = perpendicular, 1 = collapsed). Non-zero means an older build sheared
    /// this node and <see cref="FromMatrix"/> will straighten it.
    /// </summary>
    public static float ShearOf(Matrix4 m)
    {
        var r0 = m.Row0.Xyz;
        var r1 = m.Row1.Xyz;
        var r2 = m.Row2.Xyz;
        if (r0.LengthSquared < 1e-12f || r1.LengthSquared < 1e-12f || r2.LengthSquared < 1e-12f)
            return 0f;
        r0 = Vector3.Normalize(r0);
        r1 = Vector3.Normalize(r1);
        r2 = Vector3.Normalize(r2);
        return MathF.Max(
            MathF.Abs(Vector3.Dot(r0, r1)),
            MathF.Max(MathF.Abs(Vector3.Dot(r1, r2)), MathF.Abs(Vector3.Dot(r0, r2))));
    }

    /// <summary>
    /// Builds the rotation from three orthonormal basis rows.
    /// </summary>
    /// <remarks>
    /// OpenTK is internally inconsistent here: <see cref="Matrix4.CreateFromQuaternion"/> emits a
    /// row-vector matrix, but <see cref="Quaternion.FromMatrix"/> expects a column-vector one, so
    /// feeding one into the other round-trips to the <em>inverse</em> rotation. Transposing bridges
    /// them. Everything in this type is defined against <c>CreateFromQuaternion</c>, since that is
    /// what <see cref="ToMatrix"/> actually renders with.
    /// </remarks>
    private static Quaternion QuatFromRowBasis(Matrix3 rowBasis)
        => Quaternion.Normalize(Quaternion.FromMatrix(Matrix3.Transpose(rowBasis)));

    private static Vector3 Orthogonal(Vector3 v)
    {
        // Any direction not parallel to v; picking the smallest component avoids a near-zero cross.
        var seed = MathF.Abs(v.X) < MathF.Abs(v.Z) ? Vector3.UnitX : Vector3.UnitZ;
        return Vector3.Normalize(Vector3.Cross(v, seed));
    }

    // -- Axes ------------------------------------------------------------------

    /// <summary>
    /// The object's own X/Y/Z direction in the parent's space — what the red, green and blue
    /// gizmo arrows point along once the gizmo is local to the object. Unit length: scale is
    /// deliberately excluded so a squashed part still gets straight arrows.
    /// </summary>
    public Vector3 LocalAxis(int index)
    {
        var m = Matrix4.CreateFromQuaternion(Rotation);
        var v = index switch
        {
            0 => m.Row0.Xyz,
            1 => m.Row1.Xyz,
            _ => m.Row2.Xyz,
        };
        return v.LengthSquared > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitZ;
    }

    /// <summary>The pivot's position in the parent's space (same as <see cref="Position"/>).</summary>
    public Vector3 PivotInParent => Position;

    // -- Pivot edits -----------------------------------------------------------

    /// <summary>
    /// Moves the pivot to <paramref name="newOrigin"/> (a point in the mesh's own coordinate space)
    /// without the geometry moving at all. Every vertex composes to exactly the same place as
    /// before; only the point that rotation and scale work about has changed.
    /// </summary>
    /// <remarks>
    /// This one operation is the whole of Recenter Origin and every Move Origin snap: the tool
    /// moves to the mesh, never the mesh to the tool.
    /// </remarks>
    public void SetOrigin(Vector3 newOrigin)
    {
        // Wherever the new pivot point currently sits in the parent's space is where it must stay.
        Position = Vector3.TransformPosition(newOrigin, ToMatrix());
        Origin   = newOrigin;
    }

    // -- Rotation edits --------------------------------------------------------

    /// <summary>
    /// Rotates by <paramref name="radians"/> about one of the object's <em>own</em> axes, leaving
    /// that axis's direction untouched. This is what a local rotate-ring drag and an additive 90°
    /// button both do.
    /// </summary>
    public void RotateLocal(int axisIndex, float radians)
    {
        var delta = axisIndex switch
        {
            0 => Matrix4.CreateRotationX(radians),
            1 => Matrix4.CreateRotationY(radians),
            _ => Matrix4.CreateRotationZ(radians),
        };
        // Composed left-first in row-vector order, so the delta lands in the object's own frame.
        // Deliberately done through matrices rather than quaternion multiplication: the matrix
        // order matches ToMatrix by construction, so there is no second convention to get wrong.
        Compose(delta * Matrix4.CreateFromQuaternion(Rotation));
    }

    /// <summary>
    /// Rotates by <paramref name="radians"/> about a direction fixed in the parent's space —
    /// used by Drop to Floor and anything else that must mean "straight down in the world"
    /// regardless of how the part is turned.
    /// </summary>
    public void RotateInParent(Vector3 parentAxis, float radians)
    {
        if (parentAxis.LengthSquared < 1e-12f) return;
        var delta = Matrix4.CreateFromAxisAngle(Vector3.Normalize(parentAxis), radians);
        // Delta on the right = applied last = in the parent's frame.
        Compose(Matrix4.CreateFromQuaternion(Rotation) * delta);
    }

    /// <summary>Replaces <see cref="Rotation"/> from a row-vector rotation matrix.</summary>
    private void Compose(Matrix4 rowRotation)
        => Rotation = QuatFromRowBasis(new Matrix3(rowRotation));

    // -- Euler view (the number fields) ----------------------------------------

    /// <summary>
    /// Orientation as X/Y/Z degrees about the object's own axes, in the order X then Y then Z.
    /// </summary>
    /// <remarks>
    /// These are a <em>view</em> of <see cref="Rotation"/>, not the storage — which is why they can
    /// never disagree with the arrows the way the old KUKA A/B/C readout did. That convention
    /// (<c>A</c> about Z, <c>B</c> about Y, <c>C</c> about X) still exists for the robot and KRL,
    /// where it is correct; it was only ever wrong as a description of a part.
    /// </remarks>
    public Vector3 EulerDegrees
    {
        get
        {
            var m = Matrix4.CreateFromQuaternion(Rotation);
            // For R = Rx(x)*Ry(y)*Rz(z) in row-vector order:
            //   Row0 = ( cy cz,  cy sz, -sy )
            //   Row1 = ( ...  ,  ...  ,  sx cy )
            //   Row2 = ( ...  ,  ...  ,  cx cy )
            float sy   = Math.Clamp(-m.M13, -1f, 1f);
            float yRad = MathF.Asin(sy);
            float cy   = MathF.Cos(yRad);

            float xRad, zRad;
            if (MathF.Abs(cy) > 1e-6f)
            {
                xRad = MathF.Atan2(m.M23, m.M33);
                zRad = MathF.Atan2(m.M12, m.M11);
            }
            else
            {
                // Straight up or down: X and Z describe the same turn. Pin X at zero and put all
                // of it in Z rather than returning an arbitrary split.
                xRad = 0f;
                zRad = MathF.Atan2(-m.M21, m.M22);
            }

            return new Vector3(
                MathHelper.RadiansToDegrees(xRad),
                MathHelper.RadiansToDegrees(yRad),
                MathHelper.RadiansToDegrees(zRad));
        }
        set
        {
            float x = MathHelper.DegreesToRadians(value.X);
            float y = MathHelper.DegreesToRadians(value.Y);
            float z = MathHelper.DegreesToRadians(value.Z);
            Compose(Matrix4.CreateRotationX(x)
                  * Matrix4.CreateRotationY(y)
                  * Matrix4.CreateRotationZ(z));
        }
    }

    // -- Scale edits -----------------------------------------------------------

    /// <summary>Smallest scale factor allowed on any axis — zero would collapse the mesh flat
    /// and negative would mirror it, neither of which should be reachable by a fumbled keystroke.</summary>
    public const float MinScale = 1e-3f;

    /// <summary>Clamps every axis to at least <see cref="MinScale"/>.</summary>
    public void ClampScale()
        => Scale = new Vector3(
            MathF.Max(MathF.Abs(Scale.X), MinScale),
            MathF.Max(MathF.Abs(Scale.Y), MinScale),
            MathF.Max(MathF.Abs(Scale.Z), MinScale));
}
