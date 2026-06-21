using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public class MeshoptDecodeTest
{
    private static string RepoRoot =>
        AssetPaths.FindCellsDirectory() is { } cells
            ? Path.GetFullPath(Path.Combine(cells, "..", ".."))
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData("reference/MassiveCONNECT-V2/MassiveCONNECT/webcells/LFAM3/rotary_bed_bottom.glb")]
    [InlineData("reference/MassiveCONNECT-V2/MassiveCONNECT/webcells/LFAM3/rotary_bed_top.glb")]
    public void Meshopt_reference_glb_loads_via_runtime_decode(string rel)
    {
        Directory.SetCurrentDirectory(RepoRoot);
        var path = AssetPaths.Resolve(rel);
        Assert.True(File.Exists(path), $"missing: {path}");

        var bytes = File.ReadAllBytes(path);
        var json  = System.Text.Encoding.UTF8.GetString(bytes, 20, (int)BitConverter.ToUInt32(bytes, 12));
        Assert.Contains("EXT_meshopt_compression", json, StringComparison.Ordinal);
        Assert.Contains("webcells/LFAM3", path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        var scene = GltfLoader.Load(path);
        Assert.True(TriangleCount(scene) > 100_000,
            $"{rel}: expected substantial geometry after meshopt decode");
    }

    private static long TriangleCount(SceneNode root)
    {
        long total = 0;
        foreach (var n in root.SelfAndDescendants())
            if (n.PendingMesh is { } mesh)
                total += SceneTriangleStats.TriangleCount(mesh);
        return total;
    }
}