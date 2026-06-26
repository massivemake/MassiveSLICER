using System;
using System.Numerics;
using MassiveSlicer.Core.Slicing;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>Validates that integrating a normal map recovers the surface it came from.</summary>
public class NormalMapIntegratorTest(ITestOutputHelper output)
{
    [Fact]
    public void Integrate_RecoversGaussianBump_FromItsNormals()
    {
        const int n = 64;
        float cx = (n - 1) / 2f, cy = (n - 1) / 2f, sigma = n / 6f;

        // True relative height: a central Gaussian bump.
        var truth = new float[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                truth[y * n + x] = MathF.Exp(-d2 / (2f * sigma * sigma));
            }

        // Tangent-space normals from central-difference slopes of the true height.
        var normals = new Vector3[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int xm = x > 0 ? x - 1 : x, xp = x < n - 1 ? x + 1 : x;
                int ym = y > 0 ? y - 1 : y, yp = y < n - 1 ? y + 1 : y;
                float dhdx = 0.5f * (truth[y * n + xp] - truth[y * n + xm]);
                float dhdy = 0.5f * (truth[yp * n + x] - truth[ym * n + x]);
                normals[y * n + x] = Vector3.Normalize(new Vector3(-dhdx, -dhdy, 1f));
            }

        var hf = NormalMapIntegrator.Integrate(normals, n, n, iterations: 800);

        // Recovered field (normalized 0..1) should peak at the centre and be ~0 at the corners.
        float centre = hf.Sample(0.5f, 0.5f);
        float corner = hf.Sample(0.02f, 0.02f);
        output.WriteLine($"centre={centre:F3} corner={corner:F3}");
        Assert.True(centre > 0.85f, $"bump centre should be high, was {centre}");
        Assert.True(corner < 0.15f, $"flat corner should be low, was {corner}");

        // Correlate recovered vs truth (both normalized) across the grid.
        float tMin = float.MaxValue, tMax = float.MinValue;
        foreach (var v in truth) { if (v < tMin) tMin = v; if (v > tMax) tMax = v; }
        double sumErr = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float tn = (truth[y * n + x] - tMin) / (tMax - tMin);
                float rn = hf.Samples[y * n + x];
                sumErr += Math.Abs(tn - rn);
            }
        double meanAbsErr = sumErr / (n * n);
        output.WriteLine($"mean|err| = {meanAbsErr:F4}");
        Assert.True(meanAbsErr < 0.05, $"recovered surface should closely match truth, mae={meanAbsErr}");
    }
}
