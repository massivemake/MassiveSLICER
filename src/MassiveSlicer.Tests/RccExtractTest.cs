using MassiveSlicer.Core.IO;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>
/// Extracts specific robot models from RCC files to assets/rcc_models/.
/// Run manually when you need to pull meshes out of the bundled library.
/// </summary>
public class RccExtractTest(ITestOutputHelper output)
{
    [Theory]
    [InlineData("reslib_kuka_03.rcc", "external/library/KUKA/KR210_R3100_ULTRA")]
    [InlineData("reslib_kuka_01.rcc", "external/library/KUKA/KR210_R3100")]
    public void ExtractRobotModel(string fileName, string virtualPathPrefix)
    {
        string? rccPath = FindAsset(fileName);
        if (rccPath is null) { output.WriteLine($"SKIP: {fileName} not found."); return; }

        var files = RccExtractor.Extract(File.ReadAllBytes(rccPath));

        string modelName  = virtualPathPrefix.Split('/').Last();
        string outputDir  = Path.Combine(
            AppContext.BaseDirectory, "../../../../..", "assets", "rcc_models", modelName);
        Directory.CreateDirectory(outputDir);

        int written = 0;
        foreach (var (vpath, data) in files)
        {
            if (!vpath.StartsWith(virtualPathPrefix, StringComparison.Ordinal)) continue;

            string relative = vpath[(virtualPathPrefix.Length + 1)..]; // strip prefix + slash
            string dest     = Path.Combine(outputDir, relative);
            File.WriteAllBytes(dest, data);
            output.WriteLine($"  wrote {dest}  ({data.Length:N0} B)");
            written++;
        }

        output.WriteLine($"\n{fileName}: extracted {written} files to {outputDir}");
        Assert.True(written > 0, $"No entries matched prefix '{virtualPathPrefix}'");
    }

    [Theory]
    [InlineData("reslib_kuka_01.rcc")]
    [InlineData("reslib_kuka_02.rcc")]
    [InlineData("reslib_kuka_03.rcc")]
    public void ExtractAllKr120Models(string fileName)
    {
        string? rccPath = FindAsset(fileName);
        if (rccPath is null) { output.WriteLine($"SKIP: {fileName} not found."); return; }

        const string matchPrefix = "external/library/KUKA/KR120";
        var files = RccExtractor.Extract(File.ReadAllBytes(rccPath));

        int written = 0;
        foreach (var (vpath, data) in files)
        {
            if (!vpath.StartsWith(matchPrefix, StringComparison.Ordinal)) continue;

            // vpath e.g. "external/library/KUKA/KR120_R2500_pro/AXIS_1.stl"
            // → outputDir: assets/rcc_models/KR120_R2500_pro/AXIS_1.stl
            string afterPrefix = vpath["external/library/KUKA/".Length..]; // "KR120_R2500_pro/AXIS_1.stl"
            string dest = Path.Combine(
                AppContext.BaseDirectory, "../../../../..", "assets", "rcc_models", afterPrefix);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, data);
            output.WriteLine($"  {afterPrefix}  ({data.Length:N0} B)");
            written++;
        }

        output.WriteLine($"\n{fileName}: extracted {written} KR120 files");
        // Not asserting > 0 — some RCC files may not contain KR120 entries.
    }

    private static string? FindAsset(string fileName) =>
        new[]
        {
            Path.Combine(AppContext.BaseDirectory, "../../../../..", "assets", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", fileName),
        }.FirstOrDefault(File.Exists);
}
