using System;
using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Recovers a height field from a tangent-space <b>normal map</b> by solving the Poisson
/// equation over the slope field. glTF carries no displacement channel, so when a model's
/// only detail source is its normal map, this integrates it back to relative height.
/// <para>
/// A tangent-space normal <c>(nx,ny,nz)</c> encodes the surface slope of an implicit height
/// field <c>h</c>: <c>dh/dx = -nx/nz</c>, <c>dh/dy = -ny/nz</c>. We integrate that gradient
/// field by solving <c>div(grad h) = div(slopes)</c> with red-black SOR and Neumann (zero-flux)
/// boundaries, then normalize the result to 0..1. The reconstruction is <i>relative</i> (height
/// up to an additive constant and an overall scale set by the caller's displacement distance);
/// sign conventions that differ (OpenGL vs DirectX green channel) just flip the relief, which
/// the displacement UI already exposes as an Invert toggle.
/// </para>
/// </summary>
public static class NormalMapIntegrator
{
    /// <param name="normals">Tangent-space normals, row-major (row 0 = image top), length w*h.</param>
    /// <param name="iterations">SOR sweeps. ~400-800 is plenty for a downsampled (~256) map.</param>
    /// <param name="omega">Over-relaxation factor in (1,2); ~1.9 converges fastest here.</param>
    public static HeightField2D Integrate(Vector3[] normals, int width, int height,
                                          int iterations = 600, float omega = 1.9f)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("dimensions must be positive.");
        if (normals.Length != width * height)
            throw new ArgumentException($"normals length {normals.Length} != {width}*{height}.");

        int n = width * height;
        var p = new float[n];   // dh/dx
        var q = new float[n];   // dh/dy
        for (int i = 0; i < n; i++)
        {
            var nv = normals[i];
            float nz = MathF.Abs(nv.Z) < 1e-4f ? (nv.Z < 0 ? -1e-4f : 1e-4f) : nv.Z;
            p[i] = -nv.X / nz;
            q[i] = -nv.Y / nz;
        }

        // Divergence of the slope field (central differences, clamped at borders) = Poisson RHS.
        var rhs = new float[n];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int xm = x > 0 ? x - 1 : x, xp = x < width - 1 ? x + 1 : x;
                int ym = y > 0 ? y - 1 : y, yp = y < height - 1 ? y + 1 : y;
                float dpx = 0.5f * (p[y * width + xp] - p[y * width + xm]);
                float dqy = 0.5f * (q[yp * width + x] - q[ym * width + x]);
                rhs[y * width + x] = dpx + dqy;
            }

        var h = new float[n];
        // Red-black SOR: update checkerboard halves so each sweep uses fresh neighbours in parallel-safe order.
        for (int iter = 0; iter < iterations; iter++)
            for (int colour = 0; colour < 2; colour++)
                for (int y = 0; y < height; y++)
                    for (int x = (y + colour) & 1; x < width; x += 2)
                    {
                        int xm = x > 0 ? x - 1 : x, xp = x < width - 1 ? x + 1 : x;
                        int ym = y > 0 ? y - 1 : y, yp = y < height - 1 ? y + 1 : y;
                        float sum = h[y * width + xm] + h[y * width + xp]
                                  + h[ym * width + x] + h[yp * width + x];
                        float gs = 0.25f * (sum - rhs[y * width + x]);
                        int idx = y * width + x;
                        h[idx] += omega * (gs - h[idx]);
                    }

        // Normalize to 0..1 (relief is relative).
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < n; i++) { if (h[i] < min) min = h[i]; if (h[i] > max) max = h[i]; }
        float range = max - min;
        if (range > 1e-9f)
            for (int i = 0; i < n; i++) h[i] = (h[i] - min) / range;
        else
            Array.Clear(h);

        return new HeightField2D(h, width, height);
    }
}
