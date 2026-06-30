using MassiveSlicer.App;
using MassiveSlicer.Viewport.Loading;

namespace MassiveSlicer.Tests;

public sealed class StepImportTest
{
    [Theory]
    [InlineData(".stp")]
    [InlineData(".step")]
    [InlineData(".STP")]
    public void ImportHelper_recognizes_step_extensions(string ext)
    {
        Assert.True(ImportHelper.IsSupported($"part{ext}"));
    }

    [Fact]
    public void StepLoader_tessellates_rhino_step_export()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string path = @"\\192.168.0.191\MassiveFILES\Research\LFAM\New folder\PackMassive\resource\Miami_Sample_v5.stp";
        if (!File.Exists(path))
            return;

        var node = StepLoader.Load(path);
        Assert.NotNull(node.PendingMesh);
        Assert.True(node.PendingMesh!.Positions.Length >= 9);
        Assert.Equal(node.PendingMesh.Positions.Length, node.PendingMesh.Normals.Length);

        var mesh = node.PendingMesh;
        var min = new OpenTK.Mathematics.Vector3(float.MaxValue);
        var max = new OpenTK.Mathematics.Vector3(float.MinValue);
        foreach (var p in mesh.Positions)
        {
            min = OpenTK.Mathematics.Vector3.ComponentMin(min, p);
            max = OpenTK.Mathematics.Vector3.ComponentMax(max, p);
        }

        Assert.True(max.X - min.X > 1f);
        Assert.True(max.Y - min.Y > 1f);
        Assert.True(max.Z - min.Z > 1f);
    }
}