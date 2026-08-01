using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

/// <summary>
/// Pins the transform conventions the move/rotate/scale tools rely on. Several of these encode
/// behaviour that the previous single-matrix representation got wrong; the names say which.
/// </summary>
public class NodeTransformTest
{
    private const float Tol = 1e-4f;

    private static void AssertClose(Vector3 expected, Vector3 actual, float tol = Tol)
    {
        Assert.True((expected - actual).Length < tol,
            $"expected {expected}, got {actual}");
    }

    // -- Composition round-trip ------------------------------------------------

    [Fact]
    public void Compose_then_decompose_recovers_every_part()
    {
        var t = new NodeTransform(
            position: new Vector3(120f, -40f, 900f),
            rotation: Quaternion.FromAxisAngle(Vector3.Normalize(new Vector3(1f, 2f, 3f)), 0.7f),
            scale:    new Vector3(2f, 0.5f, 3f),
            origin:   new Vector3(5f, 6f, 7f));

        var back = NodeTransform.FromMatrix(t.ToMatrix(), t.Origin);

        AssertClose(t.Position, back.Position);
        AssertClose(t.Scale,    back.Scale);
        AssertClose(t.Origin,   back.Origin);
        // Compare rotations by what they do, not by raw components (q and -q are the same turn).
        AssertClose(t.LocalAxis(0), back.LocalAxis(0));
        AssertClose(t.LocalAxis(1), back.LocalAxis(1));
        AssertClose(t.LocalAxis(2), back.LocalAxis(2));
    }

    // -- The shear bug ---------------------------------------------------------

    [Fact]
    public void Uneven_scale_then_rotate_does_not_shear_the_basis()
    {
        // The exact sequence that used to wreck a part: scale unevenly, then rotate.
        var t = new NodeTransform(Vector3.Zero, Quaternion.Identity, new Vector3(3f, 0.4f, 1f), Vector3.Zero);
        t.RotateLocal(2, MathHelper.DegreesToRadians(37f));
        t.RotateLocal(0, MathHelper.DegreesToRadians(-22f));

        Assert.True(NodeTransform.ShearOf(t.ToMatrix()) < Tol,
            $"basis went out of square: shear {NodeTransform.ShearOf(t.ToMatrix())}");
    }

    [Fact]
    public void Scale_survives_rotation_exactly()
    {
        var t = new NodeTransform(Vector3.Zero, Quaternion.Identity, new Vector3(3f, 0.4f, 1f), Vector3.Zero);
        t.RotateLocal(1, MathHelper.DegreesToRadians(64f));

        // The old code read scale off the composed matrix, where rotation had already polluted it.
        var back = NodeTransform.FromMatrix(t.ToMatrix(), Vector3.Zero);
        AssertClose(new Vector3(3f, 0.4f, 1f), back.Scale);
    }

    [Fact]
    public void Scaling_the_basis_rows_of_a_rotated_matrix_is_not_itself_skew()
    {
        // Worth pinning because it is a tempting wrong conclusion: the old scale drag multiplied
        // whole basis rows of an already-rotated matrix, which only changes their lengths. Rows
        // stay perpendicular, so this operation alone never skewed a part.
        var m = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(30f));
        m.Row0 *= 3f;
        m.Row1 *= 0.4f;

        Assert.True(NodeTransform.ShearOf(m) < Tol);
    }

    [Fact]
    public void A_genuinely_skewed_matrix_is_detected_and_straightened()
    {
        // Skew does arise from composing a non-uniform scale with rotations in the wrong frame
        // (inv(R) * D * R), which is what world-space delta maths can produce. Such a matrix
        // cannot be represented here, so loading one visibly squares the part back up — callers
        // check ShearOf first so that is a warning rather than a silent geometry change.
        var r = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(30f));
        Matrix4.Invert(r, out var rInv);
        var skewed = rInv * Matrix4.CreateScale(3f, 0.4f, 1f) * r;

        Assert.True(NodeTransform.ShearOf(skewed) > 0.01f,
            $"expected detectable skew, measured {NodeTransform.ShearOf(skewed)}");

        var straightened = NodeTransform.FromMatrix(skewed, Vector3.Zero);
        Assert.True(NodeTransform.ShearOf(straightened.ToMatrix()) < Tol);
    }

    // -- Local rotation --------------------------------------------------------

    [Fact]
    public void Rotating_about_a_local_axis_leaves_that_axis_pointing_the_same_way()
    {
        var t = new NodeTransform(Vector3.Zero, Quaternion.Identity, Vector3.One, Vector3.Zero);
        t.RotateLocal(1, MathHelper.DegreesToRadians(50f));   // get it off world-aligned first
        t.RotateLocal(2, MathHelper.DegreesToRadians(25f));

        for (int axis = 0; axis < 3; axis++)
        {
            var before = t.LocalAxis(axis);
            var spun   = t;
            spun.RotateLocal(axis, MathHelper.DegreesToRadians(90f));
            AssertClose(before, spun.LocalAxis(axis));
        }
    }

    [Fact]
    public void Four_ninety_degree_local_steps_return_to_the_start()
    {
        var t = new NodeTransform(Vector3.Zero, Quaternion.Identity, Vector3.One, Vector3.Zero);
        t.RotateLocal(0, MathHelper.DegreesToRadians(15f));
        var start = t;

        for (int i = 0; i < 4; i++) t.RotateLocal(1, MathHelper.DegreesToRadians(90f));

        AssertClose(start.LocalAxis(0), t.LocalAxis(0));
        AssertClose(start.LocalAxis(1), t.LocalAxis(1));
        AssertClose(start.LocalAxis(2), t.LocalAxis(2));
    }

    // -- The A/C swap ----------------------------------------------------------

    [Fact]
    public void The_first_rotation_field_turns_the_object_about_its_own_X_axis()
    {
        // This is the defect Jeff reported as "rotating red does what green should": the red-labelled
        // field used the robot's KUKA convention, where the first value turns about Z, not X.
        var t = new NodeTransform(Vector3.Zero, Quaternion.Identity, Vector3.One, Vector3.Zero)
        {
            EulerDegrees = new Vector3(90f, 0f, 0f),
        };

        AssertClose(Vector3.UnitX, t.LocalAxis(0));   // X untouched
        AssertClose(Vector3.UnitZ, t.LocalAxis(1));   // Y swung onto Z
    }

    [Fact]
    public void Kuka_ABC_still_means_what_the_robot_needs()
    {
        // Not a bug — this convention is correct for the arm, and stays. It is only wrong as a
        // description of a part, which is what the part UI used to borrow it for.
        var m = MassiveSlicer.Core.Kinematics.KukaIkSolver.AbcToMatrix(90f, 0f, 0f);
        var xAxis = new Vector3(m.M11, m.M12, m.M13);

        // A=90 turns about Z, so the X axis swings onto Y — it does not stay put.
        AssertClose(Vector3.UnitY, xAxis);
    }

    [Fact]
    public void Euler_degrees_round_trip()
    {
        foreach (var angles in new[]
                 {
                     new Vector3(0f, 0f, 0f),
                     new Vector3(90f, 0f, 0f),
                     new Vector3(0f, 45f, 0f),
                     new Vector3(0f, 0f, -120f),
                     new Vector3(15f, -30f, 75f),
                 })
        {
            var t = new NodeTransform(Vector3.Zero, Quaternion.Identity, Vector3.One, Vector3.Zero)
            {
                EulerDegrees = angles,
            };
            AssertClose(angles, t.EulerDegrees, 1e-3f);
        }
    }

    // -- Pivot behaviour -------------------------------------------------------

    [Fact]
    public void Recentring_the_pivot_does_not_move_the_geometry()
    {
        // The one-time re-centre at import: pivot jumps to the bounding-box centre, the mesh
        // stays exactly where the file put it.
        var centre = new Vector3(250f, -80f, 15f);
        var t = NodeTransform.PivotedAt(centre);

        var m = t.ToMatrix();
        foreach (var p in new[] { Vector3.Zero, new Vector3(1f, 2f, 3f), centre })
            AssertClose(p, Vector3.TransformPosition(p, m));
    }

    [Fact]
    public void Rotation_turns_about_the_pivot_not_the_files_own_zero()
    {
        // The file left its origin 1000mm away from the part. Rotating must not fling the part
        // around that distant point — the complaint that every operation pivoted somewhere absurd.
        var centre = new Vector3(1000f, 0f, 0f);
        var t = NodeTransform.PivotedAt(centre);
        t.RotateLocal(2, MathHelper.DegreesToRadians(180f));

        var m = t.ToMatrix();
        AssertClose(centre, Vector3.TransformPosition(centre, m));
        AssertClose(new Vector3(999f, 0f, 0f), Vector3.TransformPosition(new Vector3(1001f, 0f, 0f), m));
    }

    [Fact]
    public void Moving_the_pivot_never_moves_the_geometry()
    {
        // Recenter Origin and every Move Origin snap are this one operation. Tested from an
        // already rotated and unevenly scaled state, since that is where a naive implementation
        // would shift the part.
        var t = new NodeTransform(
            position: new Vector3(300f, 40f, -12f),
            rotation: Quaternion.FromAxisAngle(Vector3.Normalize(new Vector3(2f, -1f, 4f)), 1.1f),
            scale:    new Vector3(1.7f, 0.6f, 2.3f),
            origin:   new Vector3(11f, 22f, 33f));

        var probes = new[]
        {
            Vector3.Zero, new Vector3(100f, 0f, 0f), new Vector3(-40f, 65f, 210f), t.Origin,
        };
        var before = probes.Select(p => Vector3.TransformPosition(p, t.ToMatrix())).ToArray();

        // Snap the pivot to a bounding-box corner, then a face centre, then back to the middle.
        foreach (var newOrigin in new[]
                 {
                     new Vector3(-50f, -50f, -50f), new Vector3(0f, 0f, 120f), Vector3.Zero,
                 })
        {
            t.SetOrigin(newOrigin);
            Assert.Equal(newOrigin, t.Origin);
            for (int i = 0; i < probes.Length; i++)
                AssertClose(before[i], Vector3.TransformPosition(probes[i], t.ToMatrix()), 1e-2f);
        }
    }

    [Fact]
    public void A_moved_pivot_becomes_the_point_rotation_turns_about()
    {
        // Snapping the origin to a corner and rotating must swing the part around that corner.
        var t = NodeTransform.PivotedAt(Vector3.Zero);
        t.SetOrigin(new Vector3(100f, 0f, 0f));
        t.RotateLocal(2, MathHelper.DegreesToRadians(90f));

        var m = t.ToMatrix();
        AssertClose(new Vector3(100f, 0f, 0f), Vector3.TransformPosition(new Vector3(100f, 0f, 0f), m));
        // A point 100mm further out along X swings onto +Y about that corner.
        AssertClose(new Vector3(100f, 100f, 0f), Vector3.TransformPosition(new Vector3(200f, 0f, 0f), m));
    }

    [Fact]
    public void Scale_grows_away_from_the_pivot()
    {
        // Why snapping the origin to a face matters: scaling from a corner grows the part away
        // from that corner instead of ballooning out from an arbitrary point.
        var corner = new Vector3(0f, 0f, 0f);
        var t = NodeTransform.PivotedAt(corner);
        t.Scale = new Vector3(2f, 2f, 2f);

        var m = t.ToMatrix();
        AssertClose(corner, Vector3.TransformPosition(corner, m));
        AssertClose(new Vector3(200f, 0f, 0f), Vector3.TransformPosition(new Vector3(100f, 0f, 0f), m));
    }

    [Fact]
    public void Clamping_blocks_a_collapsed_or_mirrored_scale()
    {
        var t = new NodeTransform(Vector3.Zero, Quaternion.Identity, new Vector3(0f, -2f, 1f), Vector3.Zero);
        t.ClampScale();

        Assert.True(t.Scale.X >= NodeTransform.MinScale);
        Assert.Equal(2f, t.Scale.Y, 4);
        Assert.Equal(1f, t.Scale.Z, 4);
    }

    // -- The A/B/C step buttons ------------------------------------------------
    //
    // Asserted on what the rotation DOES to the part's axes, never on EulerDegrees. The middle axis
    // of an Euler triple re-spells itself past 90 degrees, and reading the answer back through the
    // very representation that caused the bug would let it pass while still being broken.

    /// <summary>Where the part's own X/Y/Z end up pointing — a spelling-free identity for an orientation.</summary>
    private static (Vector3 X, Vector3 Y, Vector3 Z) Axes(NodeTransform t)
        => (t.LocalAxis(0), t.LocalAxis(1), t.LocalAxis(2));

    private static void AssertAxes((Vector3 X, Vector3 Y, Vector3 Z) expected, NodeTransform actual)
    {
        var got = Axes(actual);
        AssertClose(expected.X, got.X, 1e-3f);
        AssertClose(expected.Y, got.Y, 1e-3f);
        AssertClose(expected.Z, got.Z, 1e-3f);
    }

    private static NodeTransform Turned(int axisIndex, float degrees)
    {
        var t = NodeTransform.Identity;
        t.RotateInParent(
            axisIndex switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ },
            MathHelper.DegreesToRadians(degrees));
        return t;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Stepping_an_axis_four_times_returns_to_the_start(int axisIndex)
    {
        // The B-axis bug: clicking B went 90, 180, 90, 180 forever, because Y is the middle axis of
        // the X-Y-Z decomposition and only spans -90..+90, so a part at Y=180 read back as Y=0.
        // A full turn must take exactly four clicks on every axis, with no orientation repeated.
        var start = Axes(NodeTransform.Identity);
        var t = NodeTransform.Identity;

        var seen = new List<(Vector3, Vector3, Vector3)>();
        for (int click = 0; click < 3; click++)
        {
            t.SnapToAxisStop(axisIndex, reverse: false);
            seen.Add(Axes(t));
        }

        foreach (var (quarter, i) in seen.Select((s, i) => (s, i)))
            Assert.True((quarter.Item1 - start.X).Length > 1e-3f
                     || (quarter.Item2 - start.Y).Length > 1e-3f
                     || (quarter.Item3 - start.Z).Length > 1e-3f,
                $"click {i + 1} of 4 came back to the starting orientation — the axis is toggling, not turning");

        t.SnapToAxisStop(axisIndex, reverse: false);
        AssertAxes(start, t);
    }

    [Fact]
    public void Stepping_B_reaches_the_half_turn_the_euler_view_cannot_spell()
    {
        // Two clicks on B is a 180 about Y: the part's own X now points along -X, Z along -Z.
        var t = NodeTransform.Identity;
        t.SnapToAxisStop(1, reverse: false);
        t.SnapToAxisStop(1, reverse: false);

        AssertAxes((-Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ), t);
    }

    [Fact]
    public void An_off_angle_part_straightens_onto_the_next_stop_rather_than_adding_to_it()
    {
        // Jeff's locked spec: 37 degrees forward lands on 90, not 127. It is a straighten-up
        // button, not a nudge.
        var t = Turned(0, 37f);
        t.SnapToAxisStop(0, reverse: false);

        AssertAxes(Axes(Turned(0, 90f)), t);
    }

    [Fact]
    public void An_off_angle_part_stepped_backwards_straightens_down_to_the_stop_below()
    {
        var t = Turned(0, 37f);
        t.SnapToAxisStop(0, reverse: true);

        AssertAxes(Axes(NodeTransform.Identity), t);
    }

    [Fact]
    public void Stepping_past_the_nearest_stop_still_moves_the_way_it_was_asked()
    {
        // 53 degrees is nearest to 90, so forward lands there; backward has to skip past the
        // nearest stop and come down to 0, or the click would appear to do nothing in reverse.
        var forward = Turned(0, 53f);
        forward.SnapToAxisStop(0, reverse: false);
        AssertAxes(Axes(Turned(0, 90f)), forward);

        var backward = Turned(0, 53f);
        backward.SnapToAxisStop(0, reverse: true);
        AssertAxes(Axes(NodeTransform.Identity), backward);
    }

    [Fact]
    public void Stepping_squares_up_every_axis_so_a_face_meets_the_bed()
    {
        // Tumbled on all three axes at once. Whichever axis is clicked, the result must be fully
        // axis-aligned — snapping one axis alone would leave no flat face down.
        var t = NodeTransform.Identity;
        t.RotateInParent(Vector3.UnitX, MathHelper.DegreesToRadians(21f));
        t.RotateInParent(Vector3.UnitY, MathHelper.DegreesToRadians(-14f));
        t.RotateInParent(Vector3.UnitZ, MathHelper.DegreesToRadians(63f));

        t.SnapToAxisStop(1, reverse: false);

        foreach (var axis in new[] { t.LocalAxis(0), t.LocalAxis(1), t.LocalAxis(2) })
        {
            // Every axis lies along a world axis: one component is +/-1 and the rest are zero.
            float biggest = MathF.Max(MathF.Abs(axis.X), MathF.Max(MathF.Abs(axis.Y), MathF.Abs(axis.Z)));
            Assert.True(MathF.Abs(biggest - 1f) < 1e-3f,
                $"axis {axis} is not square to the world after a step");
        }
    }

    [Fact]
    public void Stepping_forwards_then_backwards_returns_to_where_it_started()
    {
        var t = Turned(1, 90f);
        var before = Axes(t);

        t.SnapToAxisStop(1, reverse: false);
        t.SnapToAxisStop(1, reverse: true);

        AssertAxes(before, t);
    }
}
