using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

/// <summary>
/// Outliner selection asks whether Explode should enable. That question must not
/// quantise every corner of a merged scan on the UI thread.
/// </summary>
public sealed class MeshConnectedComponentsTest
{
    [Fact]
    public void Two_separate_triangles_stay_explodable()
    {
        var mesh = TwoTriangles(gapMm: 50f);

        Assert.True(MeshConnectedComponents.CanProbeOnUiThread(mesh));
        Assert.True(MeshConnectedComponents.HasMultipleComponents(mesh));
        Assert.True(MeshConnectedComponents.HasMultipleComponentsForUi(mesh));
    }

    [Fact]
    public void One_triangle_is_not_explodable()
    {
        var positions = new[]
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
        };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var mesh = new MeshData(positions, normals, null, "one");

        Assert.False(MeshConnectedComponents.HasMultipleComponentsForUi(mesh));
    }

    [Fact]
    public void Mesh_over_the_corner_budget_does_not_run_the_shell_probe()
    {
        int count = MeshConnectedComponents.UiProbeMaxCorners + 1;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            // Distinct corners. A missing guard cannot collapse these into one dictionary key.
            positions[i] = new Vector3(i, (i % 3) * 10f, 0f);
            normals[i] = Vector3.UnitZ;
        }

        var mesh = new MeshData(positions, normals, null, "merged-scan");
        var start = Environment.TickCount64;
        bool multiple = MeshConnectedComponents.HasMultipleComponentsForUi(mesh);
        long elapsedMs = Environment.TickCount64 - start;

        Assert.False(MeshConnectedComponents.CanProbeOnUiThread(mesh));
        Assert.False(multiple);
        Assert.True(elapsedMs < 200, $"shell probe took {elapsedMs} ms");
    }

    private static MeshData TwoTriangles(float gapMm)
    {
        var positions = new[]
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
            new Vector3(gapMm, 0, 0), new Vector3(gapMm + 1, 0, 0), new Vector3(gapMm, 1, 0),
        };
        var normals = new Vector3[positions.Length];
        for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.UnitZ;
        return new MeshData(positions, normals, null, "two");
    }
}
