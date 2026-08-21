using System.Numerics;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Drop to Plate needs a lowest point. A mesh has vertices; an imported KRL program has only
/// move endpoints, which is why the mesh-only lookup returned nothing and the drop silently
/// did nothing. This locks the toolpath side of that measurement.
/// <para>
/// Mirrors <c>ViewportView.ToolpathMinZ</c>. The view method cannot be referenced from tests
/// (it lives in the Avalonia app), so the arithmetic is duplicated here deliberately — if one
/// changes without the other, that is the signal to look.
/// </para>
/// </summary>
public sealed class ToolpathMinZTest
{
    private static float MinZ(Toolpath tp, Matrix4x4 world, Vector3 origin = default)
    {
        float minZ = float.MaxValue;
        foreach (var layer in tp.Layers)
            foreach (var m in layer.Moves)
            {
                var a = Vector3.Transform(m.From - origin, world);
                if (a.Z < minZ) minZ = a.Z;
                var b = Vector3.Transform(m.To - origin, world);
                if (b.Z < minZ) minZ = b.Z;
            }
        return minZ;
    }

    /// <summary>
    /// How the scene actually holds a registered toolpath: points stay ABSOLUTE, the node is
    /// translated to the toolpath's centroid, and both renderer and exporter draw
    /// <c>(point − origin) × world</c>. Reproduced here because the first version of the drop
    /// transformed the raw point, double-counted the centroid, and buried the part under the bed.
    /// </summary>
    private static (Matrix4x4 World, Vector3 Origin) AsRegistered(Vector3 centroid)
        => (Matrix4x4.CreateTranslation(centroid), centroid);

    private static Toolpath Ramp(float z0, float z1)
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, z0);
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, z0), new Vector3(100, 0, z0), MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, z0), new Vector3(100, 100, z1), MoveKind.Extrude));
        tp.Layers.Add(layer);
        return tp;
    }

    [Fact]
    public void FindsTheLowestPointAcrossBothEndsOfEveryMove()
    {
        // The minimum lives on a move's To, not its From — scanning only From would miss it.
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 500f);
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 500f), new Vector3(10, 0, 120f), MoveKind.Extrude));
        tp.Layers.Add(layer);

        Assert.Equal(120f, MinZ(tp, Matrix4x4.Identity), 3);
    }

    [Fact]
    public void CountsTravelMovesToo()
    {
        // A travel that dips lower than any extrusion still collides with the bed, so the drop
        // has to respect it.
        var tp = Ramp(300f, 400f);
        tp.Layers[0].Moves.Add(
            new ToolpathMove(new Vector3(100, 100, 400f), new Vector3(0, 0, 250f), MoveKind.Travel));

        Assert.Equal(250f, MinZ(tp, Matrix4x4.Identity), 3);
    }

    [Fact]
    public void HonoursTheNodeTransform()
    {
        // An imported KRL program hangs at whatever offset its BASE implies; the drop is computed
        // in world space, so the node transform has to be applied first.
        var tp = Ramp(300f, 400f);
        var lifted = Matrix4x4.CreateTranslation(0f, 0f, 1220f);   // ~4 ft in the air

        Assert.Equal(1520f, MinZ(tp, lifted), 3);
    }

    [Fact]
    public void DropDeltaPutsTheLowestPointExactlyOnTheBed()
    {
        var tp   = Ramp(300f, 400f);
        var world = Matrix4x4.CreateTranslation(0f, 0f, 1220f);
        const float bedZ = 0f;

        float delta = bedZ - MinZ(tp, world);
        var dropped = world * Matrix4x4.CreateTranslation(0f, 0f, delta);

        Assert.Equal(bedZ, MinZ(tp, dropped), 3);
    }

    [Fact]
    public void EmptyToolpathReportsNoLowestPointRatherThanZero()
    {
        // MaxValue is the "nothing to measure" signal the caller checks; returning 0 would
        // slam an empty node onto the bed from wherever it was.
        Assert.Equal(float.MaxValue, MinZ(new Toolpath(), Matrix4x4.Identity));
    }

    [Fact]
    public void CentroidIsNotDoubleCountedForARegisteredToolpath()
    {
        // At rest a registered toolpath must measure exactly where its raw points say it is.
        var tp = Ramp(300f, 400f);
        var (world, origin) = AsRegistered(new Vector3(1500f, 900f, 350f));

        Assert.Equal(300f, MinZ(tp, world, origin), 3);
        Assert.NotEqual(300f, MinZ(tp, world), 3);   // the bug: origin ignored
    }

    [Fact]
    public void DropDeltaLandsOnTheBedForARegisteredToolpath()
    {
        var tp = Ramp(1520f, 1800f);                       // imported ~4 ft up
        var (world, origin) = AsRegistered(new Vector3(1500f, 900f, 1660f));
        const float bedZ = 300f;

        float delta   = bedZ - MinZ(tp, world, origin);
        var   dropped = world * Matrix4x4.CreateTranslation(0f, 0f, delta);

        Assert.Equal(bedZ, MinZ(tp, dropped, origin), 3);
    }
}
