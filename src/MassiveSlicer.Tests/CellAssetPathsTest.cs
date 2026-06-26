using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public class CellAssetPathsTest
{
    [Fact]
    public void Lfam1_lists_rail_robot_and_bed_assets()
    {
        var path = ResolveCellJson("LFAM1", "lfam1.json");
        if (path is null) return;

        var cell  = CellLoader.Load(path);
        var paths = CellAssetPaths.AllModelPaths(cell).ToList();

        Assert.Contains(paths, p => p.Contains("LFAM1RobotRail", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains("LFAM2Robot", StringComparison.OrdinalIgnoreCase));
        Assert.True(paths.Count >= 3);
    }

    [Fact]
    public void GltfLoader_InvalidateAsset_forces_reload_after_overwrite()
    {
        var dir  = Path.Combine(Path.GetTempPath(), "mslicer-gltf-inv-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "box.glb");
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllBytes(file, MinimalGlb(1f));
            var first  = GltfLoader.Load(file);
            var firstX = BoundsMaxX(first);

            GltfLoader.InvalidateAsset(file);
            File.WriteAllBytes(file, MinimalGlb(2f));
            var second  = GltfLoader.Load(file);
            var secondX = BoundsMaxX(second);

            Assert.True(secondX > firstX * 1.5f);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    private static float BoundsMaxX(SceneNode root)
    {
        float max = float.NegativeInfinity;
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } mesh) continue;
            foreach (var p in mesh.Positions)
                max = MathF.Max(max, p.X);
        }

        return max;
    }

    private static byte[] MinimalGlb(float halfExtent)
    {
        // Unit cube centred on origin in GLTF metres; GltfLoader scales to mm.
        float h = halfExtent;
        var positions = new float[]
        {
            -h, -h, -h,  h, -h, -h,  h,  h, -h, -h,  h, -h,
            -h, -h,  h,  h, -h,  h,  h,  h,  h, -h,  h,  h,
        };

        var indices = new ushort[]
        {
            0, 1, 2, 0, 2, 3,
            4, 6, 5, 4, 7, 6,
        };

        var posBytes = new byte[positions.Length * 4];
        Buffer.BlockCopy(positions, 0, posBytes, 0, posBytes.Length);
        var idxBytes = new byte[indices.Length * 2];
        Buffer.BlockCopy(indices, 0, idxBytes, 0, idxBytes.Length);

        var accessors = $$"""
[
  {"bufferView":0,"componentType":5126,"count":8,"type":"VEC3","max":[{{h}},{{h}},{{h}}],"min":[-{{h}},-{{h}},-{{h}}]},
  {"bufferView":1,"componentType":5123,"count":12,"type":"SCALAR"}
]
""";
        var bufferViews = $$"""
[
  {"buffer":0,"byteOffset":0,"byteLength":{{posBytes.Length}}},
  {"buffer":0,"byteOffset":{{posBytes.Length}},"byteLength":{{idxBytes.Length}}}
]
""";
        var bin = posBytes.Concat(idxBytes).ToArray();
        while (bin.Length % 4 != 0) bin = bin.Append((byte)0).ToArray();

        var json = $$"""
{"asset":{"version":"2.0"},"buffers":[{"byteLength":{{bin.Length}}}],"bufferViews":{{bufferViews}},"accessors":{{accessors}},"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],"nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
""";
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        var jsonPad   = (4 - jsonBytes.Length % 4) % 4;
        var jsonChunk = jsonBytes.Length + jsonPad;
        var binPad    = (4 - bin.Length % 4) % 4;
        var total     = 12 + 8 + jsonChunk + 8 + bin.Length + binPad;

        var outBytes = new byte[total];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(0), 0x46546C67);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(4), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(8), (uint)total);

        var o = 12;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o), (uint)jsonChunk);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o + 4), 0x4E4F534A);
        jsonBytes.CopyTo(outBytes.AsSpan(o + 8));
        if (jsonPad > 0)
            outBytes.AsSpan(o + 8 + jsonBytes.Length, jsonPad).Fill(0x20);
        o += 8 + jsonChunk;

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o), (uint)bin.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o + 4), 0x004E4942);
        bin.CopyTo(outBytes.AsSpan(o + 8));
        return outBytes;
    }

    private static string? ResolveCellJson(string folder, string file)
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "assets", "cells", folder, file),
            Path.Combine(FindRepoRoot() ?? "", "assets", "cells", folder, file),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "assets", "cells")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
            if (string.IsNullOrEmpty(dir)) break;
        }

        return null;
    }
}