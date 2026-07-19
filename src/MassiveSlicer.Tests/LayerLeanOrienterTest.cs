using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class LayerLeanOrienterTest
{
    static Toolpath TwoLayerWall(float xOffset)
    {
        // Layer 0: wall segment along Y at x=0, z=0. Layer 1: same segment at x=xOffset, z=3.
        var tp = new Toolpath();
        var l0 = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        l0.Moves.Add(new ToolpathMove(new Vector3(0, 0, 0), new Vector3(0, 100, 0), MoveKind.Extrude));
        var l1 = new ToolpathLayer(1, 3f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        l1.Moves.Add(new ToolpathMove(new Vector3(xOffset, 0, 3), new Vector3(xOffset, 100, 3), MoveKind.Extrude));
        tp.Layers.Add(l0);
        tp.Layers.Add(l1);
        return tp;
    }

    static float TiltDeg(Vector3 n) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(n), Vector3.UnitZ), -1f, 1f)) * 180f / MathF.PI;

    [Fact]
    public void Lean_SteppedWall_TiltsTowardOffset()
    {
        // 2 mm step over 3 mm layer → lean angle atan(2/3) ≈ 33.7°.
        var tp = TwoLayerWall(2f);
        LayerLeanOrienter.ApplyInPlace(tp, strength: 1f, maxTiltDeg: 90f, beadWidth: 6f);

        var n = tp.Layers[1].Moves[0].Normal;
        Assert.True(n.X > 0.01f, $"should lean +X, got {n}");
        Assert.InRange(TiltDeg(n), 32f, 35.5f);
        Assert.Equal(Vector3.Zero, tp.Layers[0].Moves[0].Normal); // first layer untouched
    }

    [Fact]
    public void Lean_MaxTilt_Caps()
    {
        var tp = TwoLayerWall(2f);
        LayerLeanOrienter.ApplyInPlace(tp, strength: 1f, maxTiltDeg: 15f, beadWidth: 6f);
        Assert.InRange(TiltDeg(tp.Layers[1].Moves[0].Normal), 14.9f, 15.1f);
    }

    [Fact]
    public void Lean_HalfStrength_HalvesAngle()
    {
        var tp = TwoLayerWall(2f);
        LayerLeanOrienter.ApplyInPlace(tp, strength: 0.5f, maxTiltDeg: 90f, beadWidth: 6f);
        Assert.InRange(TiltDeg(tp.Layers[1].Moves[0].Normal), 16f, 18f); // ≈ 33.7/2
    }

    [Fact]
    public void Lean_VerticalStack_StaysVertical()
    {
        // No horizontal offset → no lean assigned (zero normal = vertical fallback).
        var tp = TwoLayerWall(0f);
        LayerLeanOrienter.ApplyInPlace(tp, strength: 1f, maxTiltDeg: 90f, beadWidth: 6f);
        Assert.Equal(Vector3.Zero, tp.Layers[1].Moves[0].Normal);
    }

    [Fact]
    public void Lean_ZeroStrength_NoOp()
    {
        var tp = TwoLayerWall(2f);
        LayerLeanOrienter.ApplyInPlace(tp, strength: 0f, maxTiltDeg: 90f, beadWidth: 6f);
        Assert.Equal(Vector3.Zero, tp.Layers[1].Moves[0].Normal);
    }

    [Fact]
    public void Lean_PreassignedNormals_Untouched()
    {
        // Geodesic/overhang normals must not be overwritten.
        var tp = TwoLayerWall(2f);
        var custom = Vector3.Normalize(new Vector3(0, 1, 1));
        tp.Layers[1].Moves[0] = tp.Layers[1].Moves[0] with { Normal = custom };
        LayerLeanOrienter.ApplyInPlace(tp, strength: 1f, maxTiltDeg: 90f, beadWidth: 6f);
        Assert.Equal(custom, tp.Layers[1].Moves[0].Normal);
    }

    [Fact]
    public void Lean_UnsupportedIsland_StaysVertical()
    {
        // Layer-1 segment far outside the search radius → no support found → vertical.
        var tp = TwoLayerWall(500f);
        LayerLeanOrienter.ApplyInPlace(tp, strength: 1f, maxTiltDeg: 90f, beadWidth: 6f);
        Assert.Equal(Vector3.Zero, tp.Layers[1].Moves[0].Normal);
    }
}
