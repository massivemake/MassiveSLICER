using MassiveSlicer.Core.IO;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

public class RccListTest(ITestOutputHelper output)
{
    [Theory]
    [InlineData("reslib_kuka_01.rcc")]
    [InlineData("reslib_kuka_02.rcc")]
    [InlineData("reslib_kuka_03.rcc")]
    public void ListContents(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "../../../../..", "assets", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", fileName),
        };

        string? path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            output.WriteLine($"SKIP: {fileName} not found.");
            return;
        }

        var files = RccExtractor.Extract(File.ReadAllBytes(path));
        output.WriteLine($"\n{fileName} -- {files.Count} entries:");
        foreach (var (virtualPath, data) in files.OrderBy(kv => kv.Key))
            output.WriteLine($"  {virtualPath}  ({data.Length:N0} bytes)");
    }
}
