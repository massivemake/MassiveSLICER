using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class BedBoundaryOverlayTest
{
    static readonly Float3 Robroot = new(0f, 0f, 1000f);

    static BedCellConfig Lfam3Bed(
        float? heatedWidth = null,
        float? heatedDepth = null,
        float? width = 1800f,
        float? depth = 1800f,
        float? diameter = 1828.8f)
        => new()
        {
            Origin = new Float3(2135.45f, -52.54f, 916.31f),
            Width = width ?? 0f,
            Depth = depth ?? 0f,
            GridOrigin = new Float3(1235.45f, -952.5399f, 916.31f),
            BaseData = new Float3(2135.45f, -52.54f, -83.69f),
            Diameter = diameter,
            HeatedWidth = heatedWidth,
            HeatedDepth = heatedDepth,
        };

    static KrlBaseEntry[] Lfam3Bases() =>
    [
        new() { Name = "Rotary Table (Wood)", Index = 1 },
        new() { Name = "Rotary Table", Index = 2 },
        new() { Name = "HEATED-BED", Index = 6, Overlay = "rectangular" },
    ];

    [Fact]
    public void Rotary_base_keeps_polar_diameter()
    {
        var spec = BedBoundaryOverlay.Resolve(Lfam3Bed(), Robroot, 2, Lfam3Bases());
        Assert.False(spec.IsRectangular);
        Assert.Equal(1828.8f, spec.Diameter, 2);
        Assert.Equal(1800f, spec.Width, 1);
        Assert.Equal(1800f, spec.Depth, 1);
    }

    [Fact]
    public void Base_6_heated_is_rectangle_with_zero_diameter()
    {
        var bed = Lfam3Bed();
        var spec = BedBoundaryOverlay.Resolve(bed, Robroot, 6, Lfam3Bases());
        Assert.True(spec.IsRectangular);
        Assert.Equal(0f, spec.Diameter);
        Assert.Equal(1800f, spec.Width, 1);
        Assert.Equal(1800f, spec.Depth, 1);
        Assert.Equal(bed.GridOrigin!.Value.X, spec.GridCorner.X, 2);
        Assert.Equal(bed.GridOrigin.Value.Y, spec.GridCorner.Y, 2);
        Assert.Equal(bed.Origin.X, spec.Datum.X, 2);
        Assert.Equal(bed.Origin.Y, spec.Datum.Y, 2);
    }

    [Fact]
    public void Heated_name_without_overlay_field_is_rectangle()
    {
        KrlBaseEntry[] bases =
        [
            new() { Name = "Rotary Table", Index = 2 },
            new() { Name = "Heated Bed", Index = 9 },
        ];
        var spec = BedBoundaryOverlay.Resolve(Lfam3Bed(), Robroot, 9, bases);
        Assert.True(spec.IsRectangular);
        Assert.Equal(0f, spec.Diameter);
    }

    [Fact]
    public void Index_6_on_rotary_cell_is_heated_even_without_named_entry()
    {
        Assert.True(BedBoundaryOverlay.IsHeatedPrintBase(6, [], Lfam3Bed()));
        var spec = BedBoundaryOverlay.Resolve(Lfam3Bed(), Robroot, 6, []);
        Assert.True(spec.IsRectangular);
    }

    [Fact]
    public void Index_6_on_flat_cell_is_not_inferred()
    {
        var flat = new BedCellConfig
        {
            Origin = new Float3(0, 0, 0),
            Width = 3000,
            Depth = 3000,
            BaseData = new Float3(0, 0, 0),
        };
        Assert.False(BedBoundaryOverlay.IsHeatedPrintBase(6, [], flat));
        var spec = BedBoundaryOverlay.Resolve(flat, Float3.Zero, 6, []);
        Assert.True(spec.IsRectangular);
        Assert.Equal(0f, spec.Diameter);
        Assert.Equal(3000f, spec.Width, 1);
    }

    [Fact]
    public void Explicit_heated_size_wins_over_bed_width()
    {
        var bed = Lfam3Bed(heatedWidth: 2400, heatedDepth: 2100);
        var (_, _, source) = BedBoundaryOverlay.ResolveHeatedSize(bed);
        Assert.Equal("bed.heatedWidth/heatedDepth", source);

        var spec = BedBoundaryOverlay.Resolve(bed, Robroot, 6, Lfam3Bases());
        Assert.Equal(2400f, spec.Width, 1);
        Assert.Equal(2100f, spec.Depth, 1);
        Assert.Equal(0f, spec.Diameter);
        Assert.Equal(bed.Origin.X - 1200f, spec.GridCorner.X, 1);
        Assert.Equal(bed.Origin.Y - 1050f, spec.GridCorner.Y, 1);
    }

    [Fact]
    public void Mesh_aabb_used_when_json_size_missing()
    {
        var bed = Lfam3Bed(width: 0, depth: 0, diameter: 1828.8f);
        var (w, d, source) = BedBoundaryOverlay.ResolveHeatedSize(
            bed, heatedMeshSize: (1600f, 1550f));
        Assert.Equal(1600f, w, 1);
        Assert.Equal(1550f, d, 1);
        Assert.Equal("heated-bed mesh AABB", source);
    }

    [Fact]
    public void Diameter_square_used_when_no_other_size()
    {
        var bed = Lfam3Bed(width: 0, depth: 0, diameter: 1828.8f);
        var (w, d, source) = BedBoundaryOverlay.ResolveHeatedSize(bed);
        Assert.Equal(1828.8f, w, 2);
        Assert.Equal(1828.8f, d, 2);
        Assert.Equal("bed.diameter square fallback", source);
    }

    [Fact]
    public void Explicit_polar_overlay_wins_over_heated_name()
    {
        KrlBaseEntry[] bases =
        [
            new() { Name = "HEATED-looking rotary", Index = 6, Overlay = "polar" },
        ];
        Assert.False(BedBoundaryOverlay.IsHeatedPrintBase(6, bases, Lfam3Bed()));
        var spec = BedBoundaryOverlay.Resolve(Lfam3Bed(), Robroot, 6, bases);
        Assert.False(spec.IsRectangular);
        Assert.Equal(1828.8f, spec.Diameter, 2);
    }

    [Fact]
    public void Lfam3_json_base_6_resolves_to_1800_square()
    {
        var path = Path.Combine("assets", "cells", "LFAM3", "lfam3.json");
        if (!File.Exists(path))
        {
            var root = FindRepoRoot();
            if (root is not null)
                path = Path.Combine(root, "assets", "cells", "LFAM3", "lfam3.json");
        }
        Assert.True(File.Exists(path), $"Missing {path}");

        var cell = CellLoader.Load(path);
        Assert.Contains(cell.KrlBases, b => b.Index == 6 && b.Overlay == "rectangular");
        Assert.True(cell.Bed.Diameter is > 0f);

        var heated = BedBoundaryOverlay.Resolve(
            cell.Bed, cell.Robot.WorldPosition, 6, cell.KrlBases);
        Assert.True(heated.IsRectangular);
        Assert.Equal(0f, heated.Diameter);
        Assert.Equal(1800f, heated.Width, 1);
        Assert.Equal(1800f, heated.Depth, 1);

        var rotary = BedBoundaryOverlay.Resolve(
            cell.Bed, cell.Robot.WorldPosition, 2, cell.KrlBases);
        Assert.False(rotary.IsRectangular);
        Assert.Equal(cell.Bed.Diameter!.Value, rotary.Diameter, 2);
    }

    static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "assets", "cells")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
            if (string.IsNullOrEmpty(dir)) break;
        }
        return null;
    }
}
