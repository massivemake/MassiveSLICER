using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Verifies MeshIslands correctly separates physically-disconnected chunks that
/// PlanarMeshSplitter would otherwise silently glue into one mesh (the exact bug Jeff hit:
/// a spiral wall crossing one cut plane at two separate points along its curl).
/// </summary>
public sealed class MeshIslandsTest
{
    private static MeshData WeldedCube(Vector3 center)
    {
        float h = 0.5f;
        var p = new Vector3[]
        {
            center + new Vector3(-h,-h,-h), center + new Vector3( h,-h,-h),
            center + new Vector3( h, h,-h), center + new Vector3(-h, h,-h),
            center + new Vector3(-h,-h, h), center + new Vector3( h,-h, h),
            center + new Vector3( h, h, h), center + new Vector3(-h, h, h),
        };
        uint[] idx =
        [
            0,1,2, 0,2,3, 4,6,5, 4,7,6, 0,4,5, 0,5,1, 2,6,7, 2,7,3, 0,3,7, 0,7,4, 1,5,6, 1,6,2,
        ];
        var nrm = new Vector3[p.Length];
        for (int i = 0; i < p.Length; i++) nrm[i] = Vector3.Normalize(p[i] - center);
        return new MeshData(p, nrm, idx, "cube");
    }

    /// <summary>Concatenates meshes into one flat, unwelded MeshData — matching
    /// PlanarMeshSplitter's actual output shape (every triangle owns fresh vertex copies, no
    /// shared indices across triangles), unlike the neatly-indexed WeldedCube helper above.</summary>
    private static MeshData ConcatUnwelded(params MeshData[] meshes)
    {
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var idx = new List<uint>();
        foreach (var m in meshes)
        {
            int triCount = m.Indices is { Length: > 0 } ind ? ind.Length / 3 : m.Positions.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int v = m.Indices is { } mi ? (int)mi[t * 3 + c] : t * 3 + c;
                    idx.Add((uint)pos.Count);
                    pos.Add(m.Positions[v]);
                    nrm.Add(m.Normals[v]);
                }
            }
        }
        return new MeshData(pos.ToArray(), nrm.ToArray(), idx.ToArray(), "combined");
    }

    [Fact]
    public void Single_connected_mesh_returns_one_island()
    {
        var cube = WeldedCube(Vector3.Zero);
        var islands = MeshIslands.Split(cube);

        Assert.Single(islands);
    }

    [Fact]
    public void Two_separate_cubes_glued_into_one_mesh_split_into_two_islands()
    {
        var combined = ConcatUnwelded(WeldedCube(Vector3.Zero), WeldedCube(new Vector3(100, 0, 0)));

        var islands = MeshIslands.Split(combined);

        Assert.Equal(2, islands.Count);
        Assert.All(islands, m => Assert.Equal(12, (m.Indices?.Length ?? m.Positions.Length) / 3));
    }

    [Fact]
    public void Unwelded_triangles_sharing_only_vertex_positions_still_count_as_one_island()
    {
        // Two triangles forming a quad, deliberately built with NO shared index (every corner is
        // its own fresh vertex slot) but with the shared edge's two vertices at IDENTICAL
        // positions — exactly how PlanarMeshSplitter's own output is shaped. Must still be
        // detected as one connected piece via position welding, not the (absent) index sharing.
        Vector3[] pos =
        [
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), // triangle 1: shares edge (1,0,0)-(1,1,0)
            new(1, 1, 0), new(0, 1, 0), new(0, 0, 0), // triangle 2: shares edge (1,1,0)-(0,0,0)
        ];
        var nrm = new Vector3[pos.Length];
        for (int i = 0; i < nrm.Length; i++) nrm[i] = Vector3.UnitZ;
        uint[] idx = [0, 1, 2, 3, 4, 5];
        var mesh = new MeshData(pos, nrm, idx, "quad");

        var islands = MeshIslands.Split(mesh);

        Assert.Single(islands);
    }

    [Fact]
    public void Three_islands_from_two_planar_splitter_style_cuts_all_detected()
    {
        var combined = ConcatUnwelded(
            WeldedCube(Vector3.Zero), WeldedCube(new Vector3(100, 0, 0)), WeldedCube(new Vector3(0, 100, 0)));

        var islands = MeshIslands.Split(combined);

        Assert.Equal(3, islands.Count);
    }
}
