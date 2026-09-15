using MassiveSlicer.App;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class HeatedBedConfigTest
{
    [Fact]
    public void Lfam3_json_has_shop_heatedBed_pose_not_bed_origin()
    {
        var path = ResolveLfam3();
        var cell = CellLoader.Load(path);

        Assert.NotNull(cell.HeatedBed);
        Assert.Equal("HEATED-BED", cell.HeatedBed!.Name);
        Assert.Equal(6, cell.HeatedBed.KrlBaseIndex);
        Assert.Equal("assets/cells/LFAM3/lfam3_HeatedBed.glb", cell.HeatedBed.ModelPath);
        Assert.Equal(1065.37f, cell.HeatedBed.BasePos[0], 3);
        Assert.Equal(1515.7982f, cell.HeatedBed.BasePos[1], 3);
        Assert.Equal(-873.757f, cell.HeatedBed.BasePos[2], 3);
        Assert.Equal(-0.087f, cell.HeatedBed.BaseAbc[0], 3);
        Assert.Equal(0.11306581f, cell.HeatedBed.BaseAbc[1], 5);
        Assert.Equal(0.093820065f, cell.HeatedBed.BaseAbc[2], 5);

        // Hidden print-area GLB is not the heated plate.
        Assert.True(cell.Bed.Hidden);
        Assert.Contains("LFAM3Bed.glb", cell.Bed.ModelPath ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(cell.Bed.Origin.X, cell.HeatedBed.BasePos[0], 0);
    }

    [Fact]
    public void Heated_pose_is_robroot_plus_basePos_like_rotary()
    {
        var robroot = new Float3(0f, 0f, 1000f);
        float[] pos = [1065.37f, 1515.7982f, -873.757f];
        float[] abc = [-0.087f, 0.11306581f, 0.093820065f];

        var m = CellEnvironmentBuilder.KukaBaseWorldMatrix(pos, abc, robroot);
        Assert.Equal(1065.37f, m.Row3.X, 2);
        Assert.Equal(1515.7982f, m.Row3.Y, 2);
        Assert.Equal(126.243f, m.Row3.Z, 2); // 1000 + (-873.757)

        var originGuess = Matrix4.CreateTranslation(2135.45f, -52.54f, 916.31f);
        Assert.True(MathF.Abs(m.Row3.X - originGuess.Row3.X) > 500f);
    }

    [Fact]
    public void Asset_list_includes_heated_glb_not_as_bed_modelPath()
    {
        var cell = CellLoader.Load(ResolveLfam3());
        var paths = CellAssetPaths.AllModelPaths(cell).ToList();
        Assert.Contains(paths, p => p.Contains("lfam3_HeatedBed.glb", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveLfam3()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "assets", "cells", "LFAM3", "lfam3.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", "cells", "LFAM3", "lfam3.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "assets", "cells", "LFAM3", "lfam3.json"),
        ];
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var p = Path.Combine(dir, "assets", "cells", "LFAM3", "lfam3.json");
            if (File.Exists(p)) return p;
            dir = Directory.GetParent(dir)?.FullName ?? "";
            if (string.IsNullOrEmpty(dir)) break;
        }
        return candidates.First(File.Exists);
    }
}
