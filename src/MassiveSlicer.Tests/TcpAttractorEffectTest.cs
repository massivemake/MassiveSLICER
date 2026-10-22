using System.Numerics;
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
    public void Strongest_attractor_wins_when_spheres_overlap()
    {
        var tp = Path(new Vector3(0, 0, 10), new Vector3(0, 0, 10), Vector3.UnitZ);
        var result = TcpAttractorEffect.Apply(tp,
        [
            At(new Vector3(80, 0, 10), 200f, Vector3.UnitX),
            At(new Vector3(0, 0, 10), 200f, Vector3.UnitY),
        ]);
        var normal = Vector3.Normalize(result.Layers[0].Moves[0].Normal);
        Assert.True(Vector3.Dot(normal, Vector3.UnitY) > 0.999f);
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
        Assert.True(Vector3.Dot(cutter, axis) > 0.999f);
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
        Assert.True(Vector3.Dot(midCut, Vector3.UnitX) > 0.2f && Vector3.Dot(midCut, Vector3.UnitX) < 0.99f);
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
