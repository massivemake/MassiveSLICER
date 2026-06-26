using System;

namespace MassiveSlicer.Core.Models;

/// <summary>
/// A scalar field in texture/UV space (u,v in [0,1]). Holds a displacement / bump / height
/// map, or a height field integrated from a normal map. Values are normalized 0..1.
/// <para>
/// Decode-agnostic (no image dependency): the App decodes a texture into <see cref="Samples"/>;
/// Core samples it. Row 0 is the top of the source image (glTF UV origin is top-left), so
/// <c>v</c> maps directly to the image row without a flip.
/// </para>
/// </summary>
public sealed class HeightField2D
{
    /// <summary>Row-major samples, length <see cref="Width"/>*<see cref="Height"/>. Row 0 = image top (v=0).</summary>
    public float[] Samples { get; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Wrap (true) vs clamp (false) when sampling outside [0,1]. Mirrors the glTF sampler.</summary>
    public bool WrapU { get; init; } = true;
    public bool WrapV { get; init; } = true;

    public HeightField2D(float[] samples, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("HeightField2D dimensions must be positive.");
        if (samples.Length != width * height)
            throw new ArgumentException($"Samples length {samples.Length} != {width}*{height}.");
        Samples = samples;
        Width = width;
        Height = height;
    }

    /// <summary>Bilinear sample at UV (u,v). Wraps or clamps per <see cref="WrapU"/>/<see cref="WrapV"/>.</summary>
    public float Sample(float u, float v)
    {
        // Texel-centre convention: texel i covers [i, i+1), centre at i+0.5.
        float fx = u * Width - 0.5f;
        float fy = v * Height - 0.5f;

        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        float tx = fx - x0;
        float ty = fy - y0;

        int x1 = x0 + 1;
        int y1 = y0 + 1;

        x0 = WrapIndex(x0, Width, WrapU);
        x1 = WrapIndex(x1, Width, WrapU);
        y0 = WrapIndex(y0, Height, WrapV);
        y1 = WrapIndex(y1, Height, WrapV);

        float s00 = Samples[y0 * Width + x0];
        float s10 = Samples[y0 * Width + x1];
        float s01 = Samples[y1 * Width + x0];
        float s11 = Samples[y1 * Width + x1];

        float a = s00 + (s10 - s00) * tx;
        float b = s01 + (s11 - s01) * tx;
        return a + (b - a) * ty;
    }

    private static int WrapIndex(int i, int n, bool wrap)
    {
        if (wrap)
        {
            i %= n;
            if (i < 0) i += n;
            return i;
        }
        return i < 0 ? 0 : (i >= n ? n - 1 : i);
    }
}
