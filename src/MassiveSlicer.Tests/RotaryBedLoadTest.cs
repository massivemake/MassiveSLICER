using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class RotaryBedLoadTest
{
    private static string RepoRoot =>
        AssetPaths.FindCellsDirectory() is { } cells
            ? Path.GetFullPath(Path.Combine(cells, "..", ".."))
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData("reference/MassiveCONNECT-V2/MassiveCONNECT/webcells/LFAM3/rotary_bed_bottom.glb")]
    [InlineData("reference/MassiveCONNECT-V2/MassiveCONNECT/webcells/LFAM3/rotary_bed_top.glb")]
    public void Connect_rotary_glb_has_scene_scale_extent(string rel)
    {
        Directory.SetCurrentDirectory(RepoRoot);
        var path = AssetPaths.Resolve(rel);
        Assert.True(File.Exists(path), $"missing: {path}");
        Assert.Contains("rotary_bed", path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        var scene = GltfLoader.Load(path);
        var native = GltfLoader.LoadNativeMeters(path);
        var sceneExt = Extent(scene);
        var nativeExt = Extent(native);

        System.Console.WriteLine(
            $"{Path.GetFileName(rel)}: scene={sceneExt:F1}mm native={nativeExt:F3}m tris={TriangleCount(scene):N0}");

        Assert.True(sceneExt > 100f, $"{rel}: scene extent too small ({sceneExt:F2} mm)");
        Assert.True(TriangleCount(scene) > 0);
    }

    private static long TriangleCount(SceneNode root)
    {
        long total = 0;
        foreach (var n in root.SelfAndDescendants())
            if (n.PendingMesh is { } mesh)
                total += SceneTriangleStats.TriangleCount(mesh);
        return total;
    }

    private static float Extent(SceneNode root)
    {
        var (min, max) = Bounds(root);
        return (max - min).Length;
    }

    private static (Vector3 Min, Vector3 Max) Bounds(SceneNode root)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        Span<Vector3> corners = stackalloc Vector3[8];

        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } mesh) continue;
            var world        = n.WorldTransform;
            var (bMin, bMax) = mesh.LocalBounds;

            corners[0] = new(bMin.X, bMin.Y, bMin.Z); corners[1] = new(bMax.X, bMin.Y, bMin.Z);
            corners[2] = new(bMin.X, bMax.Y, bMin.Z); corners[3] = new(bMax.X, bMax.Y, bMin.Z);
            corners[4] = new(bMin.X, bMin.Y, bMax.Z); corners[5] = new(bMax.X, bMin.Y, bMax.Z);
            corners[6] = new(bMin.X, bMax.Y, bMax.Z); corners[7] = new(bMax.X, bMax.Y, bMax.Z);

            foreach (var p in corners)
            {
                var w = new Vector3(
                    p.X * world.M11 + p.Y * world.M21 + p.Z * world.M31 + world.M41,
                    p.X * world.M12 + p.Y * world.M22 + p.Z * world.M32 + world.M42,
                    p.X * world.M13 + p.Y * world.M23 + p.Z * world.M33 + world.M43);
                min = Vector3.ComponentMin(min, w);
                max = Vector3.ComponentMax(max, w);
            }
        }

        return (min, max);
    }
}