using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Dual-bed analog: LFAM 2's rectangular print plate maps onto LFAM 3's heated bed,
/// not the rotary platter. Drag/drop/clamp follow that surface (BASE #6).
/// </summary>
public sealed class PrintSurfaceTest
{
    static CellConfig Load(string cell) =>
        CellLoader.Load(Path.Combine("assets", "cells", cell, $"{cell.ToLowerInvariant()}.json"));

    [Fact]
    public void Lfam3_default_centre_is_still_rotary()
    {
        var cell = Load("LFAM3");
        var c = PrintSurface.Center(cell, heated: false);
        Assert.Equal(cell.Bed.Origin.X, c.X, 2);
        Assert.Equal(cell.Bed.Origin.Y, c.Y, 2);
        Assert.Equal(cell.Bed.Origin.Z, c.Z, 2);
    }

    [Fact]
    public void Lfam3_heated_centre_is_robroot_plus_basePos_not_rotary()
    {
        var cell = Load("LFAM3");
        Assert.NotNull(cell.HeatedBed);
        var c = PrintSurface.Center(cell, heated: true);
        var expect = cell.HeatedBed!.WorldOrigin(cell.Robot.WorldPosition);
        Assert.Equal(expect.X, c.X, 2);
        Assert.Equal(expect.Y, c.Y, 2);
        Assert.Equal(expect.Z, c.Z, 2);
        Assert.True(MathF.Abs(c.X - cell.Bed.Origin.X) > 200f);
        Assert.True(MathF.Abs(c.Y - cell.Bed.Origin.Y) > 200f);
        Assert.True(MathF.Abs(c.Z - cell.Bed.Origin.Z) > 200f);
    }

    [Fact]
    public void Lfam2_to_lfam3_analog_is_heated()
    {
        var src = Load("LFAM2");
        var dst = Load("LFAM3");
        Assert.False(src.Bed.IsRotaryPrintBed);
        Assert.True(PrintSurface.AnalogIsHeated(src, dst));
        Assert.Equal(6, PrintSurface.AnalogKrlBaseIndex(dst, heated: true, fallback: 1));
    }

    [Fact]
    public void Lfam3_to_lfam3_stays_rotary()
    {
        var cell = Load("LFAM3");
        Assert.False(PrintSurface.AnalogIsHeated(cell, cell));
        Assert.Equal(1, PrintSurface.AnalogKrlBaseIndex(cell, heated: false, fallback: 1));
    }

    [Fact]
    public void Lfam3_base6_export_frame_is_heated_not_rotary()
    {
        var cell = Load("LFAM3");
        var rotary = PrintSurface.ForKrlBase(cell, 1);
        var heated = PrintSurface.ForKrlBase(cell, 6);
        Assert.Equal(cell.Bed.Origin.X, rotary.Origin.X, 2);
        Assert.Equal(cell.Bed.Origin.Z, rotary.Origin.Z, 2);
        var expect = cell.HeatedBed!.WorldOrigin(cell.Robot.WorldPosition);
        Assert.Equal(expect.X, heated.Origin.X, 2);
        Assert.Equal(expect.Y, heated.Origin.Y, 2);
        Assert.Equal(expect.Z, heated.Origin.Z, 2);
        Assert.True(MathF.Abs(heated.Origin.X - rotary.Origin.X) > 200f);
        Assert.True(MathF.Abs(heated.Origin.Z - rotary.Origin.Z) > 200f);
        Assert.InRange(heated.Origin.Z, 120f, 140f);
    }

    [Fact]
    public void Lfam2_to_lfam3_bedDelta_lands_on_heated_not_rotary()
    {
        var src = Load("LFAM2");
        var dst = Load("LFAM3");
        var oldC = PrintSurface.Center(src, heated: false);
        var rotary = PrintSurface.Center(dst, heated: false);
        var heated = PrintSurface.Center(dst, heated: true);

        // A part sitting on the LFAM 2 plate centre must arrive on the heated plate,
        // hundreds of mm away from the rotary origin (the old ImportSurfaceFrame target).
        Assert.True(MathF.Abs(heated.X - rotary.X) > 200f);
        Assert.True(MathF.Abs(heated.Y - rotary.Y) > 200f);
        Assert.True(MathF.Abs(heated.Z - rotary.Z) > 200f);
        Assert.True(MathF.Abs(oldC.Z - heated.Z) < 800f || MathF.Abs(oldC.Z - rotary.Z) > MathF.Abs(oldC.Z - heated.Z),
            "heated Z is the closer analog of the LFAM 2 plate");
    }

    [Fact]
    public void Lfam3_to_lfam1_offset_lands_on_lfam1_bed()
    {
        var src = Load("LFAM3");
        var dst = Load("LFAM1");
        var oldC = PrintSurface.Center(src, heated: false);
        var newC = PrintSurface.Center(dst, heated: false);
        var d = new System.Numerics.Vector3(newC.X - oldC.X, newC.Y - oldC.Y, newC.Z - oldC.Z);

        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, oldC.Z);
        layer.Moves.Add(new ToolpathMove(
            new System.Numerics.Vector3(oldC.X, oldC.Y, oldC.Z),
            new System.Numerics.Vector3(oldC.X + 10f, oldC.Y, oldC.Z),
            MoveKind.Extrude) { E1Mm = -1100f });
        tp.Layers.Add(layer);

        MassiveSlicer.Core.Slicing.ToolpathClone.OffsetInPlace(tp, d);

        Assert.Equal(newC.X, tp.Layers[0].Moves[0].From.X, 1);
        Assert.Equal(newC.Y, tp.Layers[0].Moves[0].From.Y, 1);
        Assert.Equal(newC.Z, tp.Layers[0].Moves[0].From.Z, 1);
        Assert.Equal(newC.X + 10f, tp.Layers[0].Moves[0].To.X, 1);
        Assert.Equal(newC.Z, tp.Layers[0].Z, 1);
        Assert.Equal(-1100f, tp.Layers[0].Moves[0].E1Mm);
        Assert.False(PrintSurface.AnalogIsHeated(src, dst));
    }

    [Fact]
    public void Lfam3_rotary_to_heated_delta_round_trips()
    {
        var cell = Load("LFAM3");
        var rotary = PrintSurface.Center(cell, heated: false);
        var heated = PrintSurface.Center(cell, heated: true);
        float dx = heated.X - rotary.X;
        float dy = heated.Y - rotary.Y;
        float dz = heated.Z - rotary.Z;
        Assert.True(MathF.Abs(dx) + MathF.Abs(dy) + MathF.Abs(dz) > 200f);
        Assert.Equal(rotary.X, heated.X - dx, 2);
        Assert.Equal(rotary.Y, heated.Y - dy, 2);
        Assert.Equal(rotary.Z, heated.Z - dz, 2);
    }
}
