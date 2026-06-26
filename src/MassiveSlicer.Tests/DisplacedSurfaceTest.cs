using System;
using System.Linq;
using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>Validates UV-space height sampling and normal-displaced surface generation.</summary>
public class DisplacedSurfaceTest(ITestOutputHelper output)
{
    private static HeightField2D CentreBump(int n = 8)
    {
        var s = new float[n * n];
        float c = (n - 1) / 2f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                s[y * n + x] = Math.Clamp(1.5f * (1f - d), 0f, 1f);   // plateaus at 1 centre, 0 at rim
            }
        return new HeightField2D(s, n, n);
    }

    [Fact]
    public void HeightField_Bilinear_PeaksAtCentre()
    {
        var hf = CentreBump();
        Assert.True(hf.Sample(0.5f, 0.5f) > 0.9f, "centre should be high");
        Assert.True(hf.Sample(0.02f, 0.02f) < 0.2f, "corner should be low");
    }

    [Fact]
    public void FlatQuad_DisplacesAlongNormal_AndSubdivides()
    {
        // 10x10 quad in the XY plane, normals +Z, UVs covering the full map.
        Vector3[] pos = [new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0)];
        Vector3[] nrm = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        Vector2[] uv  = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        int[] idx = [0, 1, 2, 0, 2, 3];

        var r = DisplacedSurfaceBuilder.Build(pos, nrm, uv, idx, CentreBump(),
                                              displacementDistance: 5f, bias: 0f);

        Assert.True(r.VertexCount > 4, "surface should be subdivided well beyond the 4 input verts");
        Assert.Equal(r.Positions.Length, r.Normals.Length);
        Assert.Equal(r.Positions.Length, r.Uvs.Length);

        float maxZ = r.Positions.Max(p => p.Z);
        float minZ = r.Positions.Min(p => p.Z);
        output.WriteLine($"verts={r.VertexCount} tris={r.TriangleCount} z=[{minZ:F2},{maxZ:F2}]");

        Assert.InRange(maxZ, 4.0f, 5.01f);    // centre pushed up ~distance*1
        Assert.InRange(minZ, -0.01f, 1.0f);   // rim/corners barely moved

        // The bump must tilt some normals off +Z (a flat sheet would keep them all at Z=1).
        Assert.Contains(r.Normals, nv => nv.Z < 0.98f);
        // All normals stay unit-length and outward (Z > 0 for an upward bump).
        Assert.All(r.Normals, nv => Assert.InRange(nv.Length(), 0.99f, 1.01f));
    }

    [Fact]
    public void ZeroDistance_IsIdentityPositions()
    {
        Vector3[] pos = [new(0, 0, 0), new(10, 0, 0), new(10, 10, 0)];
        Vector3[] nrm = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        Vector2[] uv  = [new(0, 0), new(1, 0), new(1, 1)];
        int[] idx = [0, 1, 2];

        var r = DisplacedSurfaceBuilder.Build(pos, nrm, uv, idx, CentreBump(), displacementDistance: 0f);
        Assert.All(r.Positions, p => Assert.InRange(p.Z, -1e-4f, 1e-4f));
    }
}
