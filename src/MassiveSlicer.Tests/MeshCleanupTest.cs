using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class MeshCleanupTest
{
    [Fact]
    public void Clean_removes_duplicate_overlapping_triangles()
    {
        var mesh = new MeshData(
            positions:
            [
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0),
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0),
            ],
            normals: Enumerable.Repeat(Vector3.UnitZ, 6).ToArray(),
            indices: [0, 1, 2, 3, 4, 5],
            name: "Dup");

        var result = MeshCleanup.Clean(mesh, new MeshCleanupOptions
        {
            MergeVertices = true,
            UnifyPolygons = true,
            ForceUnify    = true,
        });

        int triCount = result.Mesh.Indices!.Length / 3;
        Assert.Equal(1, triCount);
        Assert.True(result.RemovedDuplicateTriangles >= 1);
    }

    [Fact]
    public void Clean_removes_degenerate_two_point_triangle()
    {
        var mesh = new MeshData(
            positions:
            [
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(2, 0, 0),
            ],
            normals: Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(),
            indices: [0, 1, 1],
            name: "Degenerate");

        var result = MeshCleanup.Clean(mesh, new MeshCleanupOptions
        {
            RemoveTwoPointPolygons = true,
            FixFaceNormalVectors   = true,
        });

        Assert.Empty(result.Mesh.Indices!);
        Assert.True(result.RemovedDegenerateTriangles >= 1);
    }

    [Fact]
    public void Clean_welds_coincident_vertices()
    {
        var mesh = new MeshData(
            positions:
            [
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0),
                new Vector3(0, 0, 0),
            ],
            normals: Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(),
            indices: [0, 1, 2, 3, 1, 2],
            name: "Weld");

        var result = MeshCleanup.Clean(mesh, new MeshCleanupOptions
        {
            MergeVertices = true,
            UnifyPolygons = true,
        });

        Assert.Equal(3, result.Mesh.Positions.Length);
        Assert.Equal(1, result.Mesh.Indices!.Length / 3);
    }
}