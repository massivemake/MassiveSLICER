using MassiveSlicer.App;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

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
    public void Overlay_draws_in_arctic_preview_when_bed_grid_is_on()
    {
        Assert.True(BedBoundaryOverlay.ShouldDrawOverlay(showBedGrid: true, slicePlaneViewerActive: false));
        Assert.False(BedBoundaryOverlay.ShouldDrawOverlay(showBedGrid: false, slicePlaneViewerActive: false));
        Assert.False(BedBoundaryOverlay.ShouldDrawOverlay(showBedGrid: true, slicePlaneViewerActive: true));
    }

    [Fact]
    public void Rotary_base_keeps_polar_diameter()
    {
        var spec = BedBoundaryOverlay.Resolve(Lfam3Bed(), Robroot, 2, Lfam3Bases());
        Assert.False(spec.IsRectangular);
        Assert.Equal(1828.8f, spec.Diameter, 2);
        Assert.Equal(1800f, spec.Width, 1);
        Assert.Equal(1800f, spec.Depth, 1);
        Assert.Equal("rotary/visual-grid", spec.Source);
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
        Assert.Equal("fallback", spec.Source);
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
            cell.Bed, cell.Robot.WorldPosition, 6, cell.KrlBases, heatedBed: cell.HeatedBed);
        Assert.True(heated.IsRectangular);
        Assert.Equal(0f, heated.Diameter);
        Assert.NotNull(cell.HeatedBed);
        var pose = cell.HeatedBed!.WorldOrigin(cell.Robot.WorldPosition);
        Assert.Equal(pose.X, heated.Datum.X, 2);
        Assert.Equal(pose.Y, heated.Datum.Y, 2);
        Assert.Equal(pose.Z, heated.Datum.Z, 2);
        Assert.NotEqual(cell.Bed.VisualGridCorner(cell.Robot.WorldPosition).X, heated.GridCorner.X, 0);
        Assert.NotEqual(cell.Bed.Origin.X, heated.Datum.X, 0);
        Assert.Equal("heatedBed.WorldOrigin", heated.Source);

        var rotary = BedBoundaryOverlay.Resolve(
            cell.Bed, cell.Robot.WorldPosition, 2, cell.KrlBases, heatedBed: cell.HeatedBed);
        Assert.False(rotary.IsRectangular);
        Assert.Equal(cell.Bed.Diameter!.Value, rotary.Diameter, 2);
        Assert.Equal(cell.Bed.VisualGridCorner(cell.Robot.WorldPosition).X, rotary.GridCorner.X, 2);
    }

    [Fact]
    public void HeatedBed_pose_does_not_use_rotary_visual_grid()
    {
        var heatedBed = new HeatedBedCellConfig
        {
            ModelPath = "assets/cells/LFAM3/lfam3_HeatedBed.glb",
            KrlBaseIndex = 6,
            BasePos = [1065.37f, 1515.7982f, -873.757f],
            BaseAbc = [-0.087f, 0.11306581f, 0.093820065f],
        };
        var spec = BedBoundaryOverlay.Resolve(
            Lfam3Bed(), Robroot, 6, Lfam3Bases(),
            liveWidth: 1800f, liveDepth: 1800f,
            heatedBed: heatedBed);

        Assert.True(spec.IsRectangular);
        Assert.Equal(1065.37f, spec.Datum.X, 2);
        Assert.Equal(1515.7982f, spec.Datum.Y, 2);
        Assert.Equal(126.243f, spec.Datum.Z, 2);
        Assert.Equal(1065.37f - 900f, spec.GridCorner.X, 1);
        Assert.Equal(1515.7982f - 900f, spec.GridCorner.Y, 1);
        Assert.NotEqual(Lfam3Bed().GridOrigin!.Value.X, spec.GridCorner.X, 0);
        Assert.Equal("heatedBed.WorldOrigin", spec.Source);
    }

    [Fact]
    public void Live_heated_node_origin_places_rectangle_without_json()
    {
        var live = new Float3(1065.37f, 1515.7982f, 126.243f);
        var spec = BedBoundaryOverlay.Resolve(
            Lfam3Bed(), Robroot, 6, Lfam3Bases(),
            liveWidth: 1800f, liveDepth: 1800f,
            liveHeatedOrigin: live);

        Assert.True(spec.IsRectangular);
        Assert.Equal(live.X, spec.Datum.X, 2);
        Assert.Equal(live.Y, spec.Datum.Y, 2);
        Assert.Equal(live.Z, spec.Datum.Z, 2);
        Assert.Equal(live.X - 900f, spec.GridCorner.X, 1);
        Assert.Equal(live.Y - 900f, spec.GridCorner.Y, 1);
        Assert.Equal("HeatedBed node", spec.Source);
        Assert.NotEqual(Lfam3Bed().GridOrigin!.Value.X, spec.GridCorner.X, 0);
    }

    [Fact]
    public void Rotary_like_aabb_does_not_override_heated_pose()
    {
        var heatedBed = new HeatedBedCellConfig
        {
            ModelPath = "assets/cells/LFAM3/lfam3_HeatedBed.glb",
            BasePos = [1065.37f, 1515.7982f, -873.757f],
            BaseAbc = [-0.087f, 0.11306581f, 0.093820065f],
        };
        var bed = Lfam3Bed();
        // fefe94e SB101 miss: print-area / rotary AABB won first.
        var rotaryMin = bed.GridOrigin!.Value;
        var rotaryMax = new Float3(rotaryMin.X + 1800f, rotaryMin.Y + 1800f, rotaryMin.Z);
        var spec = BedBoundaryOverlay.Resolve(
            bed, Robroot, 6, Lfam3Bases(),
            liveWidth: 1800f, liveDepth: 1800f,
            heatedBed: heatedBed,
            heatedMeshAabb: (rotaryMin, rotaryMax));

        Assert.True(spec.IsRectangular);
        Assert.Equal("heatedBed.WorldOrigin", spec.Source);
        Assert.Equal(1065.37f, spec.Datum.X, 2);
        Assert.Equal(1515.7982f, spec.Datum.Y, 2);
        Assert.Equal(126.243f, spec.Datum.Z, 2);
        Assert.True(MathF.Abs(spec.GridCorner.X - rotaryMin.X) > 200f);
        Assert.True(MathF.Abs(spec.GridCorner.Y - rotaryMin.Y) > 200f);
        Assert.True(MathF.Abs(spec.GridCorner.Z - rotaryMin.Z) > 200f);
    }

    [Fact]
    public void Local_unposed_aabb_does_not_override_heated_pose()
    {
        var heatedBed = new HeatedBedCellConfig
        {
            ModelPath = "assets/cells/LFAM3/lfam3_HeatedBed.glb",
            BasePos = [1065.37f, 1515.7982f, -873.757f],
            BaseAbc = [-0.087f, 0.11306581f, 0.093820065f],
        };
        // Wrapper-local plate (~1830×1230 at offset ~505,232) with identity world matrix.
        var spec = BedBoundaryOverlay.Resolve(
            Lfam3Bed(), Robroot, 6, Lfam3Bases(),
            heatedBed: heatedBed,
            heatedMeshAabb: (new Float3(-410f, -383f, -10f), new Float3(1420f, 847f, 20f)));

        Assert.Equal("heatedBed.WorldOrigin", spec.Source);
        Assert.Equal(1065.37f, spec.Datum.X, 2);
        Assert.Equal(1515.7982f, spec.Datum.Y, 2);
    }

    [Fact]
    public void Sb101_live_aabb_does_not_move_datum_off_world_origin()
    {
        var heatedBed = new HeatedBedCellConfig
        {
            ModelPath = "assets/cells/LFAM3/lfam3_HeatedBed.glb",
            BasePos = [1065.37f, 1515.7982f, -873.757f],
            BaseAbc = [-0.087f, 0.11306581f, 0.093820065f],
        };
        // Live 5dc4cad console: source=aabb corner=(614.8, 1058.9, 128.6)
        // datum=(1571.0, 1747.6, 128.6) size=1913x1377 — 556 mm off shop pose.
        var min = new Float3(614.8f, 1058.9f, 20f);
        var max = new Float3(614.8f + 1913f, 1058.9f + 1377f, 128.6f);
        var spec = BedBoundaryOverlay.Resolve(
            Lfam3Bed(), Robroot, 6, Lfam3Bases(),
            liveWidth: 1800f, liveDepth: 1800f,
            heatedBed: heatedBed,
            heatedMeshAabb: (min, max),
            heatedMeshSize: (1913f, 1377f));

        Assert.True(spec.IsRectangular);
        Assert.Equal("heatedBed.WorldOrigin", spec.Source);
        Assert.Equal(1065.37f, spec.Datum.X, 2);
        Assert.Equal(1515.7982f, spec.Datum.Y, 2);
        Assert.Equal(126.243f, spec.Datum.Z, 2);
        Assert.Equal(1800f, spec.Width, 1);
        Assert.Equal(1800f, spec.Depth, 1);
        Assert.Equal(1065.37f - 900f, spec.GridCorner.X, 1);
        Assert.Equal(1515.7982f - 900f, spec.GridCorner.Y, 1);
        Assert.Equal(126.243f, spec.GridCorner.Z, 1);
        Assert.True(MathF.Abs(spec.Datum.X - 1571f) > 400f);
        Assert.True(MathF.Abs(spec.Datum.Y - 1747.6f) > 200f);
    }

    [Fact]
    public void Lfam3_loaded_heated_overlay_uses_world_origin_not_mesh_aabb()
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
        Assert.NotNull(cell.HeatedBed);
        Assert.True(
            AssetPaths.Exists(cell.HeatedBed!.ModelPath)
            || File.Exists(Path.Combine(FindRepoRoot() ?? "", "assets", "cells", "LFAM3", "lfam3_HeatedBed.glb")),
            "shop lfam3_HeatedBed.glb must be present for this test");

        var payload = CellSceneLoader.Load(path, RightPanelTab.Additive, default);
        var heated = Assert.Single(payload.EnvironmentNodes, n => n.Name == "HeatedBed");
        Assert.True(SceneBounds.TryComputeSubtreeWorldAabb(heated, out var min, out var max));

        var aabb = (new Float3(min.X, min.Y, min.Z), new Float3(max.X, max.Y, max.Z));
        var spec = BedBoundaryOverlay.Resolve(
            cell.Bed, cell.Robot.WorldPosition, 6, cell.KrlBases,
            liveWidth: cell.Bed.Width, liveDepth: cell.Bed.Depth,
            heatedBed: cell.HeatedBed,
            heatedMeshAabb: aabb,
            heatedMeshSize: (max.X - min.X, max.Y - min.Y));

        var pose = cell.HeatedBed!.WorldOrigin(cell.Robot.WorldPosition);
        Assert.True(spec.IsRectangular);
        Assert.Equal("heatedBed.WorldOrigin", spec.Source);
        Assert.Equal(pose.X, spec.Datum.X, 2);
        Assert.Equal(pose.Y, spec.Datum.Y, 2);
        Assert.Equal(pose.Z, spec.Datum.Z, 2);
        Assert.Equal(cell.Bed.Width, spec.Width, 1);
        Assert.Equal(cell.Bed.Depth, spec.Depth, 1);
        var rotaryCorner = cell.Bed.VisualGridCorner(cell.Robot.WorldPosition);
        Assert.True(MathF.Abs(spec.GridCorner.X - rotaryCorner.X) > 200f);
        Assert.True(MathF.Abs(spec.GridCorner.Y - rotaryCorner.Y) > 200f);
        Assert.True(MathF.Abs(spec.GridCorner.Z - rotaryCorner.Z) > 200f);
        Assert.True(MathF.Abs(spec.Datum.X - (min.X + max.X) * 0.5f) > 200f
                    || MathF.Abs(spec.Datum.Y - (min.Y + max.Y) * 0.5f) > 200f);
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
