using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class PlanarMeshSplitterTest
{
    private static MeshData UnitCube()
    {
        // 1×1×1 cube centered at origin, Z-up.
        float h = 0.5f;
        var p = new Vector3[]
        {
            new(-h,-h,-h), new( h,-h,-h), new( h, h,-h), new(-h, h,-h),
            new(-h,-h, h), new( h,-h, h), new( h, h, h), new(-h, h, h),
        };
        // 12 triangles
        uint[] idx =
        [
            0,1,2, 0,2,3, // bottom -Z
            4,6,5, 4,7,6, // top +Z
            0,4,5, 0,5,1, // -Y
            2,6,7, 2,7,3, // +Y
            0,3,7, 0,7,4, // -X
            1,5,6, 1,6,2, // +X
        ];
        var nrm = new Vector3[p.Length];
        for (int i = 0; i < p.Length; i++)
            nrm[i] = Vector3.Normalize(p[i]);
        return new MeshData(p, nrm, idx, "cube");
    }

    [Fact]
    public void SplitCubeAtZ0YieldsTwoNonEmptyHalves()
    {
        var cube = UnitCube();
        var result = PlanarMeshSplitter.Split(cube, Vector3.Zero, Vector3.UnitZ);
        Assert.True(result.Positive.Positions.Length >= 3);
        Assert.True(result.Negative.Positions.Length >= 3);
        Assert.True(result.Positive.Indices is { Length: >= 3 });
        Assert.True(result.Negative.Indices is { Length: >= 3 });
        Assert.NotEmpty(result.CutLoops);
    }

    [Fact]
    public void ConnectorsAddGeometryToBothHalves()
    {
        var cube = UnitCube();
        // Larger cube so spacing fits.
        var scaled = Scale(cube, 80f);
        var split = PlanarMeshSplitter.Split(scaled, Vector3.Zero, Vector3.UnitZ);
        var conn = CutConnectorBuilder.Apply(
            split.Positive, split.Negative, split.CutLoops,
            Vector3.Zero, Vector3.UnitZ,
            new CutConnectorBuilder.Options { SpacingMm = 40f, TabDepthMm = 6f, BoltDiameterMm = 6f });

        Assert.True(conn.ConnectorCount >= 1);
        Assert.True(conn.PositiveWithConnectors.Positions.Length > split.Positive.Positions.Length);
        Assert.True(conn.NegativeWithConnectors.Positions.Length > split.Negative.Positions.Length);
    }

    private static MeshData Scale(MeshData m, float s)
    {
        var p = m.Positions.Select(v => v * s).ToArray();
        return new MeshData(p, m.Normals, m.Indices, m.Name);
    }
}
