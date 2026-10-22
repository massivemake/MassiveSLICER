using System.Numerics;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

public class TcpAttractorEffectTest
{
    [Fact]
    public void Lean_round_trips_through_the_body_axis()
    {
        var axis = TcpAttractorEffect.AxisFromLean(30f, 40f);
        var (x, y) = TcpAttractorEffect.LeanFromAxis(axis);
        Assert.Equal(30f, x, 3);
        Assert.Equal(40f, y, 3);
        Assert.Equal(1f, axis.Length(), 3);
    }

    [Fact]
    public void Upright_lean_is_plus_Z()
    {
        var axis = TcpAttractorEffect.AxisFromLean(0f, 0f);
        Assert.Equal(0f, axis.X, 3);
        Assert.Equal(0f, axis.Y, 3);
        Assert.Equal(1f, axis.Z, 3);
    }

    [Fact]
    public void Move_outside_the_sphere_keeps_its_normal()
    {
        var tp = Path(new Vector3(0, 0, 10), new Vector3(40, 0, 10), Vector3.UnitZ);
        var result = TcpAttractorEffect.Apply(tp, [At(new Vector3(500, 0, 10), 100f, Vector3.UnitX)]);
        Assert.Equal(Vector3.UnitZ, result.Layers[0].Moves[0].Normal);
    }

    [Fact]
    public void Move_on_the_cylinder_matches_the_cylinder_axis()
    {
        var axis = TcpAttractorEffect.AxisFromLean(25f, -15f);
        var tp = Path(new Vector3(-5, 0, 10), new Vector3(5, 0, 10), Vector3.UnitZ);
        var result = TcpAttractorEffect.Apply(tp, [At(new Vector3(0, 0, 10), 200f, axis)]);
        var normal = result.Layers[0].Moves[0].Normal;
        Assert.True(Vector3.Dot(Vector3.Normalize(normal), axis) > 0.999f);
    }

    [Fact]
    public void Halfway_to_the_edge_is_a_partial_lean()
    {
        var axis = Vector3.Normalize(new Vector3(1, 0, 1));
        var tp = Path(new Vector3(100, 0, 10), new Vector3(100, 0, 10), Vector3.UnitZ);
        var result = TcpAttractorEffect.Apply(tp, [At(new Vector3(0, 0, 10), 200f, axis)]);
        var normal = Vector3.Normalize(result.Layers[0].Moves[0].Normal);
        float toward = Vector3.Dot(normal, axis);
        float upright = Vector3.Dot(normal, Vector3.UnitZ);
        Assert.True(toward > 0.2f && toward < 0.99f);
        Assert.True(upright > 0.2f);
    }

    [Fact]
    public void Opposite_attractors_cancel_and_agreeing_ones_add()
    {
        var path = MillPath(Vector3.Zero, Vector3.UnitZ);
        var towardX = new TcpAttractor(Vector3.Zero, 400f, Vector3.UnitX);
        var towardMinusX = new TcpAttractor(Vector3.Zero, 400f, -Vector3.UnitX);
        var cancelled = TcpAttractorEffect.ApplyMill(path, [towardX, towardMinusX]).Layers[0].Moves[0];
        Assert.True(Vector3.Dot(Vector3.Normalize(cancelled.Normal), Vector3.UnitZ) > 0.99f,
            $"cancelled {cancelled.Normal}");

        // Half a radius is weight 0.5. Two of those on the same stick add to a full lean.
        var left = new TcpAttractor(new Vector3(-200f, 0f, 0f), 400f, Vector3.UnitX);
        var right = new TcpAttractor(new Vector3(200f, 0f, 0f), 400f, Vector3.UnitX);
        var alone = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(path, [left]).Layers[0].Moves[0].Normal);
        var added = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(path, [left, right]).Layers[0].Moves[0].Normal);
        var full = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(path, [towardX]).Layers[0].Moves[0].Normal);
        Assert.True(Vector3.Dot(alone, -Vector3.UnitX) < 0.8f, $"alone {alone}");
        Assert.True(Vector3.Dot(added, -Vector3.UnitX) > 0.99f, $"added {added}");
        Assert.True(Vector3.Dot(added, full) > 0.99f, $"added vs full {added} {full}");

        // A full lean against a half lean leaves the remaining half.
        var opposed = new TcpAttractor(new Vector3(200f, 0f, 0f), 400f, -Vector3.UnitX);
        var remainder = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(path, [towardX, opposed]).Layers[0].Moves[0].Normal);
        Assert.True(Vector3.Dot(remainder, -Vector3.UnitX) > 0.6f && Vector3.Dot(remainder, -Vector3.UnitX) < 0.9f,
            $"remainder {remainder}");
    }

    [Fact]
    public void Mill_move_on_the_cylinder_matches_the_cylinder_axis()
    {
        var axis = TcpAttractorEffect.AxisFromLean(25f, -15f);
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(-5, 0, 10), new Vector3(5, 0, 10), MoveKind.Mill)
            { Normal = Vector3.UnitZ });
        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var result = TcpAttractorEffect.ApplyMill(tp, [At(new Vector3(0, 0, 10), 200f, axis)]);
        var cutter = -Vector3.Normalize(result.Layers[0].Moves[0].Normal);
        // The bar's +axis is straight up at lean 0. The bit is the downward side of that line.
        Assert.True(Vector3.Dot(cutter, -axis) > 0.999f);
    }

    [Fact]
    public void Mill_36deg_bar_leans_the_bit_36deg_off_world_down()
    {
        var axis = TcpAttractorEffect.AxisFromLean(0f, 36f);
        var tp = MillPath(new Vector3(0, 0, 10), Vector3.UnitZ);
        var cutter = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(tp, [At(new Vector3(0, 0, 10), 200f, axis)])
                .Layers[0].Moves[0].Normal);
        float fromDown = MathF.Acos(Math.Clamp(Vector3.Dot(cutter, -Vector3.UnitZ), -1f, 1f)) * (180f / MathF.PI);
        Assert.InRange(fromDown, 35f, 37f);
        Assert.True(Vector3.Dot(cutter, -axis) > 0.999f);
    }

    [Fact]
    public void Mill_cutter_leans_toward_the_cylinder_as_it_gets_closer()
    {
        var far = MillPath(new Vector3(500, 0, 10), Vector3.UnitZ);
        var farCut = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(far, [At(new Vector3(0, 0, 10), 100f, Vector3.UnitX)])
                .Layers[0].Moves[0].Normal);
        Assert.True(Vector3.Dot(farCut, -Vector3.UnitZ) > 0.999f);

        var mid = MillPath(new Vector3(100, 0, 10), Vector3.UnitZ);
        var midCut = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(mid, [At(new Vector3(0, 0, 10), 200f, Vector3.UnitX)])
                .Layers[0].Moves[0].Normal);
        Assert.True(Vector3.Dot(midCut, -Vector3.UnitX) > 0.2f && Vector3.Dot(midCut, -Vector3.UnitX) < 0.99f);
        Assert.True(midCut.Z < -0.2f);
    }

    [Fact]
    public void Mill_and_layer_stitch_are_not_tilted()
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(1, 0, 10), MoveKind.Mill)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(1, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ, IsLayerStitch = true });
        var tp = new Toolpath();
        tp.Layers.Add(layer);

        var result = TcpAttractorEffect.Apply(tp, [At(new Vector3(0, 0, 10), 200f, Vector3.UnitX)]);
        Assert.Equal(Vector3.UnitZ, result.Layers[0].Moves[0].Normal);
        Assert.Equal(Vector3.UnitZ, result.Layers[0].Moves[1].Normal);
    }

    [Fact]
    public void Zero_normal_uses_the_layer_plane_until_the_attractor_pulls_it()
    {
        var tp = Path(new Vector3(0, 0, 10), new Vector3(0, 0, 10), Vector3.Zero);
        var result = TcpAttractorEffect.Apply(tp, [At(new Vector3(0, 0, 10), 100f, Vector3.UnitX)]);
        Assert.True(Vector3.Dot(Vector3.Normalize(result.Layers[0].Moves[0].Normal), Vector3.UnitX) > 0.999f);
    }

    [Fact]
    public void Cylinder_reaches_the_corner_a_sphere_misses()
    {
        var corner = new Vector3(80f, 0f, 80f);
        Assert.Equal(0f, TcpAttractorEffect.Influence(corner, Vector3.Zero, 100f));
        Assert.True(TcpAttractorEffect.CylinderInfluence(corner, Vector3.Zero, Vector3.UnitZ, 100f) > 0f);
        Assert.Equal(0f, TcpAttractorEffect.CylinderInfluence(new Vector3(100f, 0f, 0f), Vector3.Zero, Vector3.UnitZ, 100f));
        Assert.Equal(0f, TcpAttractorEffect.CylinderInfluence(new Vector3(0f, 0f, 101f), Vector3.Zero, Vector3.UnitZ, 100f));
        Assert.Equal(1f, TcpAttractorEffect.CylinderInfluence(Vector3.Zero, Vector3.Zero, Vector3.UnitZ, 100f), 3);

        var axis = Vector3.UnitX;
        var sphere = new TcpAttractor(Vector3.Zero, 100f, axis);
        var cylinder = sphere with { Shape = TcpAdjustShape.Cylinder };
        var sphereCut = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(MillPath(corner, Vector3.UnitZ), [sphere]).Layers[0].Moves[0].Normal);
        var cylCut = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(MillPath(corner, Vector3.UnitZ), [cylinder]).Layers[0].Moves[0].Normal);
        Assert.True(Vector3.Dot(sphereCut, -Vector3.UnitZ) > 0.999f);
        Assert.True(Vector3.Dot(cylCut, -axis) > 0.05f);
    }

    [Fact]
    public void Sphere_deflector_ignores_the_bar_and_turns_an_inward_cutter_out()
    {
        var tip = new Vector3(0f, 0f, 50f);
        var towardX = new TcpAttractor(Vector3.Zero, 200f, Vector3.UnitX, Deflect: true);
        var towardY = towardX with { Axis = Vector3.UnitY };
        // Stored +Z means the cutter points -Z, straight at the sphere center below the tip.
        var cutX = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(MillPath(tip, Vector3.UnitZ), [towardX]).Layers[0].Moves[0].Normal);
        var cutY = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(MillPath(tip, Vector3.UnitZ), [towardY]).Layers[0].Moves[0].Normal);
        Assert.True(Vector3.Dot(cutX, cutY) > 0.999f);
        Assert.True(Vector3.Dot(cutX, Vector3.UnitZ) > 0.5f);
    }

    [Fact]
    public void Sphere_deflector_leaves_a_cutter_that_already_points_out()
    {
        var tip = new Vector3(0f, 0f, 50f);
        var deflect = new TcpAttractor(Vector3.Zero, 200f, Vector3.UnitX, Deflect: true);
        // Stored -Z means the cutter points +Z, away from the center.
        var normal = TcpAttractorEffect.ApplyMill(MillPath(tip, -Vector3.UnitZ), [deflect]).Layers[0].Moves[0].Normal;
        Assert.True(Vector3.Dot(Vector3.Normalize(normal), -Vector3.UnitZ) > 0.999f);
    }

    [Fact]
    public void Travel_deeper_into_a_deflector_leans_harder_than_a_stationary_cut()
    {
        var center = Vector3.Zero;
        var stillAt = new Vector3(100f, 0f, 0f);
        var deflect = new TcpAttractor(center, 200f, Vector3.UnitZ, Deflect: true);
        var still = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(MillPath(stillAt, Vector3.UnitZ), [deflect]).Layers[0].Moves[0].Normal);
        var moving = MillPath(stillAt, Vector3.UnitZ);
        moving.Layers[0].Moves[0] = moving.Layers[0].Moves[0] with { To = new Vector3(40f, 0f, 0f) };
        var traveled = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(moving, [deflect]).Layers[0].Moves[0].Normal);
        Assert.True(Vector3.Dot(traveled, Vector3.UnitX) > Vector3.Dot(still, Vector3.UnitX) + 0.05f);
    }

    [Fact]
    public void Flange_deflector_swings_the_wrist_toward_the_exit_and_ignores_the_bar()
    {
        var tip = new Vector3(200f, 0f, 0f);
        var alongX = new TcpAttractor(new Vector3(0f, 0f, 400f), 500f, Vector3.UnitX,
            Frame: TcpAdjustFrame.Flange, Deflect: true);
        var alongY = alongX with { Axis = Vector3.UnitY };
        var normalX = Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(MillPath(tip, Vector3.UnitZ), [alongX]).Layers[0].Moves[0].Normal);
        var normalY = Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(MillPath(tip, Vector3.UnitZ), [alongY]).Layers[0].Moves[0].Normal);
        Assert.True(Vector3.Dot(normalX, normalY) > 0.999f);
        Assert.True(normalX.X > 0.1f);
    }

    [Fact]
    public void Cylinder_outward_is_the_nearest_wall_or_cap()
    {
        var wall = new TcpAttractor(Vector3.Zero, 100f, Vector3.UnitZ, TcpAdjustShape.Cylinder, Deflect: true);
        var side = TcpAttractorEffect.VolumeOutward(new Vector3(50f, 0f, 0f), wall);
        Assert.True(Vector3.Dot(side, Vector3.UnitX) > 0.999f);
        var cap = TcpAttractorEffect.VolumeOutward(new Vector3(0f, 0f, 80f), wall);
        Assert.True(Vector3.Dot(cap, Vector3.UnitZ) > 0.999f);
        Assert.Equal(0f, TcpAttractorEffect.VolumeOutward(Vector3.Zero, wall).Length());
    }

    [Fact]
    public void Pose_round_trips_shape_frame_and_deflect()
    {
        var pose = TcpAttractorPose.From(new TcpAttractor(
            new Vector3(1, 2, 3), 80f, Vector3.UnitY,
            TcpAdjustShape.Cylinder, TcpAdjustFrame.Flange, Deflect: true));
        var back = pose.ToAttractor();
        Assert.Equal(TcpAdjustShape.Cylinder, back.Shape);
        Assert.Equal(TcpAdjustFrame.Flange, back.Frame);
        Assert.True(back.Deflect);
        Assert.Equal(80f, back.RadiusMm, 3);

        var old = new TcpAttractorPose { X = 4, Y = 5, Z = 6, RadiusMm = 50, AxisZ = 1 };
        var loaded = old.ToAttractor();
        Assert.Equal(TcpAdjustShape.Sphere, loaded.Shape);
        Assert.Equal(TcpAdjustFrame.Tool, loaded.Frame);
        Assert.True(loaded.AimUnit.Z > 0.999f);
        Assert.False(loaded.Deflect);

        var aimed = TcpAttractorPose.From(new TcpAttractor(
            new Vector3(1, 2, 3), 40f, Vector3.UnitZ, AimDir: Vector3.UnitX));
        Assert.True(aimed.ToAttractor().AimUnit.X > 0.999f);
        Assert.Equal(0f, loaded.HeightMm);

        var legacyAxis = new TcpAttractorPose { X = 1, Y = 2, Z = 3, RadiusMm = 40, AxisZ = 1, Aim = "Y" };
        Assert.True(legacyAxis.ToAttractor().AimUnit.Y > 0.999f);
        var diagonal = new TcpAttractorPose
        {
            X = 1, Y = 2, Z = 3, RadiusMm = 40, AxisZ = 1,
            Aim = "Z", AimY = 1f, AimZ = 1f,
        };
        var diagonalAim = diagonal.ToAttractor().AimUnit;
        Assert.True(diagonalAim.Y > 0.7f && diagonalAim.Z > 0.7f);

        var sized = TcpAttractorPose.From(new TcpAttractor(
            new Vector3(1, 2, 3), 30f, Vector3.UnitZ,
            TcpAdjustShape.Cylinder, Deflect: true, HeightMm: 640f));
        Assert.Equal(640f, sized.ToAttractor().HeightMm, 3);
        Assert.False(sized.ToAttractor().Detract);

        var away = TcpAttractorPose.From(new TcpAttractor(
            new Vector3(1, 2, 3), 40f, Vector3.UnitZ, Detract: true));
        Assert.True(away.ToAttractor().Detract);
    }

    [Fact]
    public void Detractor_points_the_cutter_up_the_stick()
    {
        var axis = Vector3.UnitX;
        var towardHandle = new TcpAttractor(Vector3.Zero, 400f, axis);
        var awayHandle = towardHandle with { Detract = true };
        var toward = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [towardHandle])
                .Layers[0].Moves[0].Normal);
        var away = -Vector3.Normalize(
            TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [awayHandle])
                .Layers[0].Moves[0].Normal);
        Assert.True(Vector3.Dot(toward, -axis) > 0.99f, $"toward {toward}");
        Assert.True(Vector3.Dot(away, axis) > 0.99f, $"away {away}");
        Assert.Equal(
            TcpAttractorEffect.Influence(Vector3.Zero, towardHandle),
            TcpAttractorEffect.Influence(Vector3.Zero, awayHandle));
    }

    [Fact]
    public void Influence_arrow_matches_the_tcp_arrow_at_full_weight()
    {
        var axis = Vector3.UnitX;
        var tool = new TcpAttractor(Vector3.Zero, 400f, axis);
        var away = tool with { Detract = true };
        var flange = new TcpAttractor(new Vector3(0f, 0f, 400f), 400f, axis,
            Frame: TcpAdjustFrame.Flange, AimDir: Vector3.UnitY);

        Assert.True(Vector3.Dot(TcpAttractorEffect.InfluenceDirection(tool), -axis) > 0.99f);
        Assert.True(Vector3.Dot(TcpAttractorEffect.InfluenceDirection(away), axis) > 0.99f);
        Assert.True(Vector3.Dot(TcpAttractorEffect.InfluenceDirection(flange), axis) > 0.99f);
        Assert.Equal(Vector3.Zero, TcpAttractorEffect.InfluenceDirection(tool with { Deflect = true }));

        var toward = TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [tool])
            .Layers[0].Moves[0];
        var towardZ = KukaOrientation.MillToolAxes(toward.Normal, toward.TcpYawDeg).Z;
        Assert.True(Vector3.Dot(towardZ, TcpAttractorEffect.InfluenceDirection(tool)) > 0.99f);

        var detracted = TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [away])
            .Layers[0].Moves[0];
        var detractedZ = KukaOrientation.MillToolAxes(detracted.Normal, detracted.TcpYawDeg).Z;
        Assert.True(Vector3.Dot(detractedZ, TcpAttractorEffect.InfluenceDirection(away)) > 0.99f);

        var wrist = TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [flange])
            .Layers[0].Moves[0];
        var (x, _, _) = KukaOrientation.MillToolAxes(wrist.Normal, wrist.TcpYawDeg);
        Assert.True(Vector3.Dot(-x, TcpAttractorEffect.InfluenceDirection(flange)) > 0.99f);
    }

    [Fact]
    public void Deflector_cylinder_height_reaches_past_the_equal_radius()
    {
        var tip = new Vector3(0f, 0f, 200f);
        var legacy = new TcpAttractor(Vector3.Zero, 40f, Vector3.UnitZ,
            TcpAdjustShape.Cylinder, Deflect: true);
        var tall = legacy with { HeightMm = 500f };

        Assert.Equal(0f, TcpAttractorEffect.Influence(tip, legacy));
        Assert.True(TcpAttractorEffect.Influence(tip, tall) > 0.5f);
        Assert.Equal(0f, TcpAttractorEffect.Influence(new Vector3(50f, 0f, 0f), tall));

        var untouched = TcpAttractorEffect.ApplyMill(MillPath(tip, Vector3.UnitZ), [legacy])
            .Layers[0].Moves[0].Normal;
        var pushed = TcpAttractorEffect.ApplyMill(MillPath(tip, Vector3.UnitZ), [tall])
            .Layers[0].Moves[0].Normal;
        Assert.True(Vector3.Dot(Vector3.Normalize(untouched), Vector3.UnitZ) > 0.999f);
        Assert.True(Vector3.Dot(Vector3.Normalize(pushed), -Vector3.UnitZ) > 0.5f);
    }

    [Fact]
    public void Deflector_cylinder_diameter_does_not_stretch_the_height()
    {
        var thin = new TcpAttractor(Vector3.Zero, 30f, Vector3.UnitZ,
            TcpAdjustShape.Cylinder, Deflect: true, HeightMm: 400f);
        var beside = new Vector3(50f, 0f, 0f);
        var up = new Vector3(0f, 0f, 150f);
        Assert.Equal(0f, TcpAttractorEffect.Influence(beside, thin));
        Assert.True(TcpAttractorEffect.Influence(up, thin) > 0.5f);
        Assert.Equal(0f, TcpAttractorEffect.Influence(up, thin with { HeightMm = 0f }));
    }

    [Fact]
    public void Attractor_cylinder_height_reaches_past_the_equal_radius()
    {
        var along = new Vector3(0f, 0f, 200f);
        var beside = new Vector3(50f, 0f, 0f);
        var legacy = new TcpAttractor(Vector3.Zero, 40f, Vector3.UnitZ, TcpAdjustShape.Cylinder);
        var tall = legacy with { HeightMm = 500f };
        var wide = tall with { RadiusMm = 80f };
        var sphere = new TcpAttractor(Vector3.Zero, 40f, Vector3.UnitZ, HeightMm: 500f);

        Assert.Equal(0f, TcpAttractorEffect.Influence(along, legacy));
        Assert.True(TcpAttractorEffect.Influence(along, tall) > 0.5f);
        Assert.Equal(0f, TcpAttractorEffect.Influence(beside, tall));
        Assert.True(TcpAttractorEffect.Influence(along, wide) > 0.5f);
        Assert.Equal(0f, TcpAttractorEffect.Influence(new Vector3(0f, 0f, 300f), wide));
        Assert.Equal(0f, TcpAttractorEffect.Influence(along, sphere));

        var untouched = TcpAttractorEffect.ApplyMill(MillPath(along, Vector3.UnitX), [legacy])
            .Layers[0].Moves[0].Normal;
        var leaned = TcpAttractorEffect.ApplyMill(MillPath(along, Vector3.UnitX), [tall])
            .Layers[0].Moves[0].Normal;
        Assert.True(Vector3.Dot(Vector3.Normalize(untouched), Vector3.UnitX) > 0.999f);
        Assert.True(Vector3.Dot(Vector3.Normalize(leaned), Vector3.UnitZ) > 0.99f);
    }

    [Fact]
    public void Short_cylinder_deflector_exits_through_the_cap()
    {
        var puck = new TcpAttractor(Vector3.Zero, 200f, Vector3.UnitZ,
            TcpAdjustShape.Cylinder, Deflect: true, HeightMm: 40f);
        // Beside the axis, the equal-radius cylinder would exit through the wall.
        // The short height makes the cap the nearer way out.
        var cap = TcpAttractorEffect.VolumeOutward(new Vector3(150f, 0f, 15f), puck);
        Assert.True(Vector3.Dot(cap, Vector3.UnitZ) > 0.999f);
        var wall = TcpAttractorEffect.VolumeOutward(new Vector3(190f, 0f, 2f), puck);
        Assert.True(Vector3.Dot(wall, Vector3.UnitX) > 0.999f);
    }

    [Fact]
    public void Mill_tool_X_points_down_the_stick_and_keeps_the_cutter()
    {
        var attractor = new TcpAttractor(Vector3.Zero, 400f, Vector3.UnitX, AimDir: Vector3.UnitX);
        var move = TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [attractor])
            .Layers[0].Moves[0];
        var (x, _, z) = KukaOrientation.MillToolAxes(move.Normal, move.TcpYawDeg);
        Assert.True(Vector3.Dot(x, -Vector3.UnitX) > 0.99f, $"tool X {x}");
        Assert.True(Vector3.Dot(z, -Vector3.UnitZ) > 0.99f, $"tool Z {z}");
    }

    [Fact]
    public void Mill_tool_Y_points_down_the_stick()
    {
        var attractor = new TcpAttractor(Vector3.Zero, 400f, Vector3.UnitX, AimDir: Vector3.UnitY);
        var move = TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [attractor])
            .Layers[0].Moves[0];
        var (_, y, z) = KukaOrientation.MillToolAxes(move.Normal, move.TcpYawDeg);
        Assert.True(Vector3.Dot(y, -Vector3.UnitX) > 0.99f, $"tool Y {y}");
        Assert.True(Vector3.Dot(z, -Vector3.UnitZ) > 0.99f, $"tool Z {z}");
    }

    [Fact]
    public void Mill_flange_Y_points_along_the_stick()
    {
        var attractor = new TcpAttractor(new Vector3(0f, 0f, 400f), 400f, Vector3.UnitX,
            Frame: TcpAdjustFrame.Flange, AimDir: Vector3.UnitY);
        var move = TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [attractor])
            .Layers[0].Moves[0];
        var (x, _, z) = KukaOrientation.MillToolAxes(move.Normal, move.TcpYawDeg);
        var flangeY = -x;
        Assert.True(Vector3.Dot(flangeY, Vector3.UnitX) > 0.99f, $"flange Y {flangeY}");
        Assert.True(Vector3.Dot(z, -Vector3.UnitZ) > 0.99f, $"tool Z {z}");
    }

    [Fact]
    public void Mill_Z_aim_leaves_the_yaw_alone()
    {
        var attractor = new TcpAttractor(Vector3.Zero, 400f, Vector3.UnitX);
        var move = TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [attractor])
            .Layers[0].Moves[0];
        Assert.Equal(0f, move.TcpYawDeg);
        Assert.True(Vector3.Dot(Vector3.Normalize(move.Normal), Vector3.UnitX) > 0.99f);
    }

    [Fact]
    public void Mill_tool_unit_vector_points_down_the_stick()
    {
        var aim = Vector3.Normalize(new Vector3(0f, 1f, 1f));
        var attractor = new TcpAttractor(Vector3.Zero, 400f, Vector3.UnitX, AimDir: aim);
        var move = TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [attractor])
            .Layers[0].Moves[0];
        var (x, y, z) = KukaOrientation.MillToolAxes(move.Normal, move.TcpYawDeg);
        var world = aim.X * x + aim.Y * y + aim.Z * z;
        Assert.True(Vector3.Dot(world, -Vector3.UnitX) > 0.99f, $"aim {world}");
    }

    [Fact]
    public void Mill_tool_negative_X_points_up_the_stick()
    {
        var attractor = new TcpAttractor(Vector3.Zero, 400f, Vector3.UnitX, AimDir: -Vector3.UnitX);
        var move = TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [attractor])
            .Layers[0].Moves[0];
        var (x, _, _) = KukaOrientation.MillToolAxes(move.Normal, move.TcpYawDeg);
        Assert.True(Vector3.Dot(x, Vector3.UnitX) > 0.99f, $"tool X {x}");
    }

    [Fact]
    public void Mill_flange_unit_vector_points_along_the_stick()
    {
        var aim = Vector3.Normalize(new Vector3(0f, 1f, 1f));
        var attractor = new TcpAttractor(new Vector3(0f, 0f, 400f), 400f, Vector3.UnitX,
            Frame: TcpAdjustFrame.Flange, AimDir: aim);
        var move = TcpAttractorEffect.ApplyMill(MillPath(Vector3.Zero, Vector3.UnitZ), [attractor])
            .Layers[0].Moves[0];
        var (x, y, z) = KukaOrientation.MillToolAxes(move.Normal, move.TcpYawDeg);
        var flangeDir = -aim.Y * x + aim.Z * y - aim.X * z;
        Assert.True(Vector3.Dot(flangeDir, Vector3.UnitX) > 0.99f, $"flange {flangeDir}");
    }

    static TcpAttractor At(Vector3 position, float radius, Vector3 axis)
        => new(position, radius, axis);

    static Toolpath MillPath(Vector3 at, Vector3 surfaceNormal)
    {
        var layer = new ToolpathLayer(0, at.Z) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(at, at, MoveKind.Mill) { Normal = surfaceNormal });
        var tp = new Toolpath();
        tp.Layers.Add(layer);
        return tp;
    }

    static Toolpath Path(Vector3 from, Vector3 to, Vector3 normal)
    {
        var layer = new ToolpathLayer(0, 10f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(from, to, MoveKind.Extrude) { Normal = normal });
        var tp = new Toolpath();
        tp.Layers.Add(layer);
        return tp;
    }
}
