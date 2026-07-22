using System.IO;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// TEMPORARY — reproduces a live "Apply does nothing" report using the exact real mesh and exact
/// parameters read off the running app via its console bridge, without touching the live session.
/// Delete once the root cause is found and fixed.
/// </summary>
public sealed class LiveApplyReproTest
{
    private static MeshData LoadStl(string path)
    {
        var buf = File.ReadAllBytes(path);
        int triCount = BitConverter.ToInt32(buf, 80);
        var pos = new Vector3[triCount * 3];
        var nrm = new Vector3[triCount * 3];
        var idx = new uint[triCount * 3];
        int offset = 84;
        for (int t = 0; t < triCount; t++)
        {
            offset += 12; // skip stored normal
            for (int c = 0; c < 3; c++)
            {
                float x = BitConverter.ToSingle(buf, offset);
                float y = BitConverter.ToSingle(buf, offset + 4);
                float z = BitConverter.ToSingle(buf, offset + 8);
                pos[t * 3 + c] = new Vector3(x, y, z);
                nrm[t * 3 + c] = Vector3.UnitZ;
                idx[t * 3 + c] = (uint)(t * 3 + c);
                offset += 12;
            }
            offset += 2;
        }
        return new MeshData(pos, nrm, idx, "repro");
    }

    [Fact]
    public void Repro_bounded_cut_on_real_mesh_with_live_reported_parameters()
    {
        var mesh = LoadStl(@"D:\MASSIVE\Slicer Objects and Docs\Small Bendy Wall 1.stl");
        Assert.Equal(9216 * 3, mesh.Positions.Length);

        // Exact values read off the live app via transform-debug/modifier-node-debug this session.
        var localPt   = new Vector3(1157.001f, -1168.758f, 538.3719f);
        var localN    = new Vector3(0.75f, 0.661f, 0f);
        var tangentU  = new Vector3(-0.661f, 0.75f, 0f);
        var tangentV  = new Vector3(0f, 0f, 1f);
        float halfX = 1500f / 2f; // SizeX = 1500
        float halfY = 2000f / 2f; // SizeY = 2000

        var result = BoundedCutSplitter.Split(mesh, localPt, localN, tangentU, tangentV, halfX, halfY);

        Assert.NotNull(result);
        Assert.True(result!.Count >= 1);
        foreach (var m in result)
            Assert.True(m.Positions.Length > 0, "empty piece produced");
    }

    [Fact]
    public void Repro_infinite_cut_on_real_mesh_with_live_reported_parameters()
    {
        var mesh = LoadStl(@"D:\MASSIVE\Slicer Objects and Docs\Small Bendy Wall 1.stl");
        var localPt = new Vector3(1157.001f, -1168.758f, 538.3719f);
        var localN  = new Vector3(0.75f, 0.661f, 0f);

        var split = PlanarMeshSplitter.Split(mesh, localPt, localN);
        bool crosses = split.Positive.Positions.Length > 0 && split.Negative.Positions.Length > 0;

        Assert.True(crosses,
            $"Positive tris={(split.Positive.Indices?.Length ?? 0) / 3}, Negative tris={(split.Negative.Indices?.Length ?? 0) / 3}");
    }
}
