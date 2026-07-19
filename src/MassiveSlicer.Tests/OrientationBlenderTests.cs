using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class OrientationBlenderTests
{
    [Fact]
    public void BlendNormal_ZeroStrength_ReturnsUnitZ()
    {
        var surface = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var result = OrientationBlender.BlendNormal(surface, 0f);
        Assert.Equal(Vector3.UnitZ, result);
    }

    [Fact]
    public void BlendNormal_FullStrength_PreservesSurfaceNormal()
    {
        var surface = Vector3.Normalize(new Vector3(0.2f, 0.3f, 0.9f));
        var result = OrientationBlender.BlendNormal(surface, 1f);
        Assert.True(Vector3.Dot(result, surface) > 0.999f);
    }

    [Fact]
    public void BlendNormal_HalfStrength_HalvesTiltAngle()
    {
        var surface = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        float fullTilt = MathF.Acos(Vector3.Dot(surface, Vector3.UnitZ));
        var half = OrientationBlender.BlendNormal(surface, 0.5f);
        float halfTilt = MathF.Acos(Vector3.Dot(half, Vector3.UnitZ));
        Assert.InRange(halfTilt, fullTilt * 0.49f, fullTilt * 0.51f);
    }

    [Fact]
    public void BlendNormal_MaxTiltCap_ClampsTiltAngle()
    {
        // 45° surface tilt, full strength, capped at 20° → result tilts exactly 20°.
        var surface = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var capped = OrientationBlender.BlendNormal(surface, 1f, maxTiltDeg: 20f);
        float tiltDeg = MathF.Acos(Math.Clamp(Vector3.Dot(capped, Vector3.UnitZ), -1f, 1f)) * 180f / MathF.PI;
        Assert.InRange(tiltDeg, 19.9f, 20.1f);
    }

    [Fact]
    public void BlendNormal_MaxTiltCap_NoEffectBelowCap()
    {
        // 45° tilt at 10% strength = 4.5°, well under a 20° cap → cap is a no-op.
        var surface = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var free   = OrientationBlender.BlendNormal(surface, 0.1f);
        var capped = OrientationBlender.BlendNormal(surface, 0.1f, maxTiltDeg: 20f);
        Assert.True(Vector3.Dot(free, capped) > 0.9999f);
    }

    [Fact]
    public void ApplyInPlace_FirstLayerZeroTilt_VerticalFirstLayerOnly()
    {
        var surface = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var tp = new Toolpath();
        for (int li = 0; li < 2; li++)
        {
            var layer = new ToolpathLayer(li, li * 3f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
            layer.Moves.Add(new ToolpathMove(Vector3.Zero, Vector3.UnitX, MoveKind.Extrude) { Normal = surface });
            tp.Layers.Add(layer);
        }

        OrientationBlender.ApplyInPlace(tp, 1f, maxTiltDeg: 90f, firstLayerZeroTilt: true);

        Assert.Equal(Vector3.UnitZ, tp.Layers[0].Moves[0].Normal);                     // layer 1 forced vertical
        Assert.True(Vector3.Dot(tp.Layers[1].Moves[0].Normal, surface) > 0.999f);      // layer 2 untouched
    }

    [Fact]
    public void ApplyInPlace_FullStrengthWithCap_StillClamps()
    {
        // strength=1 used to early-return; with a cap it must still rewrite normals.
        var surface = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var layer = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, Vector3.UnitX, MoveKind.Extrude) { Normal = surface });
        var tp = new Toolpath();
        tp.Layers.Add(layer);

        OrientationBlender.ApplyInPlace(tp, 1f, maxTiltDeg: 20f);

        float tiltDeg = MathF.Acos(Math.Clamp(Vector3.Dot(tp.Layers[0].Moves[0].Normal, Vector3.UnitZ), -1f, 1f)) * 180f / MathF.PI;
        Assert.InRange(tiltDeg, 19.9f, 20.1f);
    }

    [Fact]
    public void ApplyInPlace_ModifiesCutMoveNormals()
    {
        var surface = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0f);
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, Vector3.UnitX, MoveKind.Extrude)
        {
            Normal = surface,
        });
        tp.Layers.Add(layer);

        OrientationBlender.ApplyInPlace(tp, 0f);
        Assert.Equal(Vector3.UnitZ, layer.Moves[0].Normal);
    }

    [Fact]
    public void CloneThenBlend_LeavesSourceUntouched()
    {
        var surface = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0f);
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, Vector3.UnitX, MoveKind.Extrude)
        {
            Normal = surface,
        });
        tp.Layers.Add(layer);

        var clone = ToolpathClone.Copy(tp);
        OrientationBlender.ApplyInPlace(clone, 0f);
        Assert.Equal(surface, layer.Moves[0].Normal);
        Assert.Equal(Vector3.UnitZ, clone.Layers[0].Moves[0].Normal);
    }
}