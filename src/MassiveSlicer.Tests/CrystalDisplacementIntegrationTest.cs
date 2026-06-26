using System;
using System.Linq;
using System.Numerics;
using MassiveSlicer.App.Services;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>
/// End-to-end on a real PBR model: load a normal-mapped GLB, integrate its normal map to a
/// height field, and displace the low-poly mesh. Proves the full PrintScanMill displacement
/// pipeline works on actual model data (not just synthetic fixtures). Opt-in: skips cleanly
/// when the model is absent, so it never breaks CI.
/// </summary>
public class CrystalDisplacementIntegrationTest(ITestOutputHelper output)
{
    // Override with the MSLICER_TEST_GLB env var; defaults to the crystal model used in dev.
    private static string ModelPath =>
        Environment.GetEnvironmentVariable("MSLICER_TEST_GLB")
        ?? @"C:\Users\MassiveMAKE\Downloads\crystal_stone_rock(1).glb";

    [Fact]
    public void Crystal_NormalMap_DisplacesLowPolyMesh()
    {
        string path = ModelPath;
        if (!System.IO.File.Exists(path))
        {
            output.WriteLine($"SKIP: model not found at {path}");
            return;
        }

        var scene = GltfLoader.Load(path);
        MeshData? mesh = scene.SelfAndDescendants()
            .Select(n => n.PendingMesh)
            .FirstOrDefault(m => m is { Uvs: not null } && m.Material?.Normal is not null);

        Assert.True(mesh is not null, "model has no UV-mapped mesh with a normal map");
        Assert.NotNull(mesh!.Uvs);
        var normalTex = mesh.Material!.Normal!;
        output.WriteLine($"mesh: {mesh.Positions.Length} verts, normal map {normalTex.Width}x{normalTex.Height}");

        // Map -> height field (the real adapter path).
        var height = PbrHeightFieldFactory.FromNormalMap(normalTex);

        // Convert to Core types.
        var pos = Array.ConvertAll(mesh.Positions, p => new Vector3(p.X, p.Y, p.Z));
        var nrm = Array.ConvertAll(mesh.Normals,   p => new Vector3(p.X, p.Y, p.Z));
        var uv  = Array.ConvertAll(mesh.Uvs!,      t => new Vector2(t.X, t.Y));
        int[] idx = mesh.Indices is { } mi
            ? Array.ConvertAll(mi, u => (int)u)
            : Enumerable.Range(0, mesh.Positions.Length).ToArray();

        const float distance = 5f;
        var flat      = DisplacedSurfaceBuilder.Build(pos, nrm, uv, idx, height, 0f);
        var displaced = DisplacedSurfaceBuilder.Build(pos, nrm, uv, idx, height, distance);

        // Same tessellation, so vertices correspond 1:1 between the two builds.
        Assert.Equal(flat.VertexCount, displaced.VertexCount);
        Assert.True(displaced.VertexCount > mesh.Positions.Length,
            "surface should be subdivided beyond the low-poly input");

        float maxMove = 0f, sumMove = 0f;
        for (int i = 0; i < displaced.VertexCount; i++)
        {
            float d = Vector3.Distance(displaced.Positions[i], flat.Positions[i]);
            if (d > maxMove) maxMove = d;
            sumMove += d;
        }
        float meanMove = sumMove / displaced.VertexCount;
        output.WriteLine($"displaced {displaced.VertexCount:N0} verts, {displaced.TriangleCount:N0} tris; " +
                         $"move max={maxMove:F3} mean={meanMove:F3} mm @ {distance} mm");

        // Detail actually moved geometry, and never beyond the displacement distance (height in 0..1).
        Assert.True(maxMove > 0.25f, $"normal-map detail should displace the surface, max move {maxMove}");
        Assert.True(maxMove <= distance + 1e-3f, $"displacement exceeded the set distance: {maxMove}");
        Assert.True(meanMove > 0.01f, "displacement should affect a meaningful fraction of the surface");
    }
}
