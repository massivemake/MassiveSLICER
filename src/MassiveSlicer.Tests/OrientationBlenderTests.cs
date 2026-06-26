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