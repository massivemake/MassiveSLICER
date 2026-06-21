using MassiveSlicer.Viewport.Loading;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>Validates GLB import for stylized meshes (geometry + scalar PBR factors).</summary>
public class GltfImportTest(ITestOutputHelper output)
{
    [Fact]
    public void CrystalStoneGlb_LoadsWithGeometryAndMaterials()
    {
        var path = ResolveCrystalTestAsset();
        if (path is null)
        {
            output.WriteLine("SKIP: crystal_stone_rock GLB not found.");
            return;
        }

        var report = GltfImportInspector.InspectFile(path);
        foreach (var line in report.ToLogLines())
            output.WriteLine(line);

        Assert.True(report.MeshCount > 0, "Expected at least one mesh.");
        Assert.True(report.VertexCount > 0, "Expected vertices.");
        Assert.True(report.TriangleCount > 0, "Expected triangles.");

        var first = report.Meshes[0];
        Assert.InRange(first.BaseColor.X, 0f, 1f);
        Assert.InRange(first.Metallic, 0f, 1f);
        Assert.InRange(first.Roughness, 0f, 1f);

        output.WriteLine(report.MaterialTextureChannels.Count > 0
            ? "PASS: stylized GLB loads; texture channels detected for future PBR work."
            : "PASS: stylized GLB loads with scalar materials only.");
    }

    [Fact]
    public void HvExtruderGlb_LoadsForRegression()
    {
        var path = ResolveRepoAsset("assets", "cells", "LFAM3", "Toolheads", "hv_extruder.glb");
        if (path is null)
        {
            output.WriteLine("SKIP: hv_extruder.glb not found.");
            return;
        }

        var report = GltfImportInspector.InspectFile(path);
        foreach (var line in report.ToLogLines())
            output.WriteLine(line);

        Assert.True(report.MeshCount > 0);
        Assert.True(report.VertexCount > 100);
    }

    private static string? ResolveCrystalTestAsset()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "assets", "test", "crystal_stone_rock.glb"),
            @"C:\Users\MassiveMAKE\Downloads\crystal_stone_rock(1).glb",
            @"C:\Users\MassiveMAKE\Downloads\crystal_stone_rock.glb",
        ];

        string? repoRoot = FindRepoRoot();
        if (repoRoot is not null)
            candidates = [Path.Combine(repoRoot, "assets", "test", "crystal_stone_rock.glb"), ..candidates];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveRepoAsset(params string[] parts)
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return null;

        var path = Path.Combine([repoRoot, ..parts]);
        return File.Exists(path) ? path : null;
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "MassiveSLICER V2.sln"))
                || Directory.Exists(Path.Combine(dir, "assets", "cells")))
                return dir;

            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }

        return null;
    }
}