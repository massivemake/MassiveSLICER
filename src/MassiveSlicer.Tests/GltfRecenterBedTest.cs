using MassiveSlicer.App;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class GltfRecenterBedTest
{
    [Fact]
    public void RecenterPivotToBottomCenter_GltfUnderBed_PreservesWorldBounds()
    {
        var path = ResolveCrystalTestAsset();
        var cellPath = ResolveLfam2Cell();
        if (path is null || cellPath is null) return;

        var cell = CellLoader.Load(cellPath);
        var root = GltfLoader.Load(path);
        ImportHelper.PlaceOnBed(root, cell);

        var meshOrigin = cell.Bed.VisualMeshOrigin(cell.Robot.WorldPosition);
        var bed = new SceneNode
        {
            Name = "bed_root",
            LocalTransform = Matrix4.CreateTranslation(meshOrigin.X, meshOrigin.Y, meshOrigin.Z),
        };
        var world = root.WorldTransform;
        root.LocalTransform = world * bed.WorldTransform.Inverted();
        bed.AddChild(root);

        var (wMinBefore, wMaxBefore) = WorldAabb(root);

        Assert.True(ImportHelper.RecenterPivotToBottomCenter(root));

        var (wMinAfter, wMaxAfter) = WorldAabb(root);

        Assert.Equal(wMinBefore.X, wMinAfter.X, 1f);
        Assert.Equal(wMinBefore.Y, wMinAfter.Y, 1f);
        Assert.Equal(wMinBefore.Z, wMinAfter.Z, 1f);
        Assert.Equal(wMaxBefore.X, wMaxAfter.X, 1f);
        Assert.Equal(wMaxBefore.Y, wMaxAfter.Y, 1f);
        Assert.Equal(wMaxBefore.Z, wMaxAfter.Z, 1f);
    }

    [Fact]
    public void RecenterPivotToBottomCenter_UndoSnapshot_RestoresTransformsAndMeshes()
    {
        var path = ResolveCrystalTestAsset();
        if (path is null) return;

        var root = GltfLoader.Load(path);
        var beforeTransforms = ImportHelper.SnapshotSubtreeTransforms(root);
        var beforeMeshes     = ImportHelper.SnapshotSubtreeMeshes(root);

        Assert.True(ImportHelper.RecenterPivotToBottomCenter(root));
        Assert.NotEqual(beforeTransforms[root], root.LocalTransform);

        ImportHelper.RestoreSubtreeSnapshot(root, beforeTransforms, beforeMeshes);

        foreach (var (node, local) in beforeTransforms)
            Assert.Equal(local, node.LocalTransform);
        foreach (var (node, mesh) in beforeMeshes)
        {
            if (mesh is null) continue;
            Assert.NotNull(node.PendingMesh);
            Assert.Equal(mesh.Positions.Length, node.PendingMesh!.Positions.Length);
        }
    }

    private static (Vector3 Min, Vector3 Max) WorldAabb(SceneNode root)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var n in root.SelfAndDescendants())
        {
            var mesh = n.PendingMesh;
            if (mesh is null) continue;
            var w = n.WorldTransform;
            foreach (var p in mesh.Positions)
            {
                var pt = new Vector3(
                    p.X * w.M11 + p.Y * w.M21 + p.Z * w.M31 + w.M41,
                    p.X * w.M12 + p.Y * w.M22 + p.Z * w.M32 + w.M42,
                    p.X * w.M13 + p.Y * w.M23 + p.Z * w.M33 + w.M43);
                min = Vector3.ComponentMin(min, pt);
                max = Vector3.ComponentMax(max, pt);
            }
        }
        return (min, max);
    }

    private static string? ResolveCrystalTestAsset() => ResolveRepoAsset("assets", "test", "crystal_stone_rock.glb");

    private static string? ResolveLfam2Cell() => ResolveRepoAsset("assets", "cells", "LFAM2", "lfam2.json");

    private static string? ResolveRepoAsset(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var path = Path.Combine([dir, ..parts]);
            if (File.Exists(path)) return path;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return null;
    }
}