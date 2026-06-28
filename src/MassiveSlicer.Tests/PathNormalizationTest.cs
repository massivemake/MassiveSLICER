using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public sealed class PathNormalizationTest
{
    [Fact]
    public void Normalize_strips_UNC_extended_prefix()
    {
        var normalized = PathNormalization.Normalize(
            @"\\?\UNC\192.168.0.191\MassiveFILES\Projects\test.mass");

        Assert.StartsWith(@"\\192.168.0.191\MassiveFILES", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\?\UNC", normalized, StringComparison.Ordinal);
    }
}