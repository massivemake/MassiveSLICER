using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public class MaterialPresetsLoaderTest
{
    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string AppOutputDir =>
        Path.GetFullPath(Path.Combine(RepoRoot, "src", "MassiveSlicer.App", "bin", "Debug", "net8.0-windows"));

    [Fact]
    public void Load_finds_presets_when_cwd_is_exe_dir_with_partial_assets_folder()
    {
        Assert.True(Directory.Exists(Path.Combine(AppOutputDir, "assets")),
            "expected deployed assets/ beside the exe (cells/krl copy)");

        Directory.SetCurrentDirectory(AppOutputDir);

        var presets = MaterialPresetsLoader.Load();

        Assert.NotEmpty(presets);
        Assert.Contains(presets, p => p.Name == "PETG - Clear");

        var asaGf = Assert.Single(presets, p => p.Name == "ASA GF - Black");
        Assert.Equal("ASA", asaGf.MaterialType);
        Assert.Equal(0.4115, asaGf.FlowRate, precision: 4);
    }
}