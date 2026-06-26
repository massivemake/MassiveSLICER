using System;
using System.IO;
using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Viewport.Scene;
using StbImageSharp;

namespace MassiveSlicer.App.Services;

/// <summary>
/// Builds a UV-space <see cref="HeightField2D"/> from a model's detail maps, for displacing the
/// low-poly mesh into the surface a multi-axis mill follows. Two sources, in preference order:
/// a supplied grayscale displacement/bump/height image (exact), or the model's embedded glTF
/// <b>normal map</b> integrated to height (approximate — glTF has no displacement channel).
/// Lives in the App layer so Core stays image-free. Row 0 = image top (glTF UV origin).
/// </summary>
public static class PbrHeightFieldFactory
{
    /// <summary>Decode a grayscale displacement / bump / height image into a height field.</summary>
    public static HeightField2D FromImage(string path, bool invert = false)
    {
        var bytes = File.ReadAllBytes(path);
        var img   = ImageResult.FromMemory(bytes, ColorComponents.Grey); // 1 byte/pixel
        int w = img.Width, h = img.Height;

        var samples = new float[w * h];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = img.Data[i] / 255f;       // row 0 = image top: matches HeightField2D + glTF UV
            samples[i] = invert ? 1f - v : v;
        }
        return new HeightField2D(samples, w, h);
    }

    /// <summary>
    /// Integrate a tangent-space normal map into a relative height field. The map is downsampled
    /// (box average) to at most <paramref name="targetMax"/> on its long edge to keep the Poisson
    /// solve fast; detail finer than that is recovered at sub-texel scale by the surface builder.
    /// </summary>
    public static HeightField2D FromNormalMap(TextureData normal, int targetMax = 256,
                                              bool invert = false, int iterations = 600)
    {
        int sw = normal.Width, sh = normal.Height;
        int tw = sw, th = sh;
        if (Math.Max(sw, sh) > targetMax)
        {
            float scale = (float)targetMax / Math.Max(sw, sh);
            tw = Math.Max(1, (int)MathF.Round(sw * scale));
            th = Math.Max(1, (int)MathF.Round(sh * scale));
        }

        var normals = new Vector3[tw * th];
        for (int ty = 0; ty < th; ty++)
            for (int tx = 0; tx < tw; tx++)
            {
                // Box-average the source block this target texel covers (normal maps are linear data).
                int x0 = tx * sw / tw, x1 = Math.Max(x0 + 1, (tx + 1) * sw / tw);
                int y0 = ty * sh / th, y1 = Math.Max(y0 + 1, (ty + 1) * sh / th);
                float ax = 0, ay = 0, az = 0; int cnt = 0;
                for (int sy = y0; sy < y1 && sy < sh; sy++)
                    for (int sx = x0; sx < x1 && sx < sw; sx++)
                    {
                        int p = (sy * sw + sx) * 4;
                        ax += normal.Pixels[p]     / 255f * 2f - 1f;
                        ay += normal.Pixels[p + 1] / 255f * 2f - 1f;
                        az += normal.Pixels[p + 2] / 255f * 2f - 1f;
                        cnt++;
                    }
                var v = cnt > 0 ? new Vector3(ax, ay, az) / cnt : Vector3.UnitZ;
                if (invert) { v.X = -v.X; v.Y = -v.Y; }
                normals[ty * tw + tx] = v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : Vector3.UnitZ;
            }

        return NormalMapIntegrator.Integrate(normals, tw, th, iterations);
    }

    /// <summary>
    /// Best-available height field for a material: a supplied image wins; otherwise integrate the
    /// normal map. Returns null when the material carries no usable detail source.
    /// </summary>
    public static HeightField2D? FromMaterial(MaterialData? material, string? displacementImagePath,
                                              bool invert = false)
    {
        if (!string.IsNullOrWhiteSpace(displacementImagePath) && File.Exists(displacementImagePath))
            return FromImage(displacementImagePath, invert);
        if (material?.Normal is { } nrm)
            return FromNormalMap(nrm, invert: invert);
        return null;
    }
}
