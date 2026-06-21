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
    public void CrystalStoneGlb_ExtractsPbrUvsTangentsAndTextures()
    {
        var path = ResolveCrystalTestAsset();
        if (path is null)
        {
            output.WriteLine("SKIP: crystal_stone_rock GLB not found.");
            return;
        }

        var root = GltfLoader.Load(path);
        var meshes = root.SelfAndDescendants()
                         .Select(n => n.PendingMesh)
                         .Where(m => m is not null)
                         .Select(m => m!)
                         .ToList();

        Assert.NotEmpty(meshes);

        var textured = meshes.FirstOrDefault(m => m.Material?.HasAnyTexture == true);
        Assert.True(textured is not null, "Expected at least one mesh with material textures.");

        Assert.NotNull(textured!.Uvs);
        Assert.Equal(textured.Positions.Length, textured.Uvs!.Length);
        Assert.NotNull(textured.Tangents);
        Assert.Equal(textured.Positions.Length, textured.Tangents!.Length);

        var mat = textured.Material!;
        Assert.NotNull(mat.BaseColor);
        Assert.True(mat.BaseColor!.IsSrgb, "Base colour must be sRGB.");
        Assert.True(mat.BaseColor.Pixels.Length == mat.BaseColor.Width * mat.BaseColor.Height * 4,
            "Decoded base colour must be tightly packed RGBA8.");
        if (mat.Normal is not null)
            Assert.False(mat.Normal.IsSrgb, "Normal map must be linear.");
        if (mat.MetallicRoughness is not null)
            Assert.False(mat.MetallicRoughness.IsSrgb, "Metallic-roughness must be linear.");

        output.WriteLine($"PASS: textured mesh '{textured.Name}' verts={textured.Positions.Length} " +
            $"baseColor={mat.BaseColor.Width}x{mat.BaseColor.Height} " +
            $"normal={(mat.Normal is null ? "-" : $"{mat.Normal.Width}x{mat.Normal.Height}")} " +
            $"mr={(mat.MetallicRoughness is null ? "-" : "yes")} ao={(mat.Occlusion is null ? "-" : "yes")} " +
            $"emissive={(mat.Emissive is null ? "-" : "yes")} doubleSided={mat.DoubleSided}");
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