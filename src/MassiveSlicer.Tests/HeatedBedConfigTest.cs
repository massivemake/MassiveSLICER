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
    public void Every_checked_in_lfam3_json_has_heatedBed_and_base_6()
    {
        var files = EnumerateLfam3JsonCopies().ToList();
        Assert.True(files.Count >= 3, "expected repo + App Assets + tests copies");
        foreach (var path in files)
        {
            var cell = CellLoader.Load(path);
            Assert.True(cell.HeatedBed is not null, $"{path} missing heatedBed");
            Assert.Contains(cell.KrlBases, b => b.Index == 6 && b.Name == "HEATED-BED");
            Assert.Contains(cell.KrlBases, b => b.Index == 1);
            Assert.Contains(cell.KrlBases, b => b.Index == 2);
            Assert.Equal(1065.37f, cell.HeatedBed!.BasePos[0], 3);
            Assert.Equal(1515.7982f, cell.HeatedBed.BasePos[1], 3);
            Assert.Equal(-873.757f, cell.HeatedBed.BasePos[2], 3);
        }
    }

    [Fact]
    public void Stripped_two_base_json_still_exposes_heated_base_6()
    {
        var rotaryOnly = new[]
        {
            new KrlBaseEntry { Name = "Rotary Table (Wood)", Index = 1 },
            new KrlBaseEntry { Name = "Rotary Table", Index = 2 },
        };
        var heated = new HeatedBedCellConfig
        {
            ModelPath = "assets/cells/LFAM3/lfam3_HeatedBed.glb",
            KrlBaseIndex = 6,
            BasePos = [1065.37f, 1515.7982f, -873.757f],
            BaseAbc = [-0.087f, 0.11306581f, 0.093820065f],
        };
        var bed = new BedCellConfig
        {
            Origin = Float3.Zero,
            BaseData = Float3.Zero,
            Width = 1800,
            Depth = 1800,
            Diameter = 1828.8f,
        };
        var bases = CellConfig.EnsureHeatedKrlBase(rotaryOnly, heated, bed);
        Assert.Equal(3, bases.Count);
        Assert.Contains(bases, b => b.Index == 6 && b.Name == "HEATED-BED" && b.Overlay == "rectangular");
        Assert.Equal("Rotary Table (Wood)", bases[0].Name);
        Assert.Equal("Rotary Table", bases[1].Name);
    }

    [Fact]
    public void Asset_list_includes_heated_glb_not_as_bed_modelPath()
    {
        var cell = CellLoader.Load(ResolveLfam3());
        var paths = CellAssetPaths.AllModelPaths(cell).ToList();
        Assert.Contains(paths, p => p.Contains("lfam3_HeatedBed.glb", StringComparison.OrdinalIgnoreCase));
    }

    static IEnumerable<string> EnumerateLfam3JsonCopies()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory,
                 })
        {
            var dir = root;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                string[] rels =
                [
                    Path.Combine(dir, "assets", "cells", "LFAM3", "lfam3.json"),
                    Path.Combine(dir, "src", "assets", "cells", "LFAM3", "lfam3.json"),
                    Path.Combine(dir, "src", "MassiveSlicer.App", "Assets", "cells", "LFAM3", "lfam3.json"),
                    Path.Combine(dir, "src", "MassiveSlicer.Tests", "assets", "cells", "LFAM3", "lfam3.json"),
                ];
                foreach (var p in rels)
                    if (File.Exists(p) && seen.Add(Path.GetFullPath(p)))
                        yield return Path.GetFullPath(p);
                dir = Directory.GetParent(dir)?.FullName ?? "";
            }
        }
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
