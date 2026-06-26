using System.IO;
using MassiveSlicer.Core.Models;
using StbImageSharp;

namespace MassiveSlicer.App.Services;

/// <summary>
/// Decodes a grayscale relief/heightmap image into a <see cref="ReliefMap"/>. Lives in the App
/// layer so Core stays image-free. White = high surface (least material removed).
/// </summary>
internal static class ReliefMapLoader
{
    public static ReliefMap LoadFromImage(
        string path, float originX, float originY, float widthMm, float lengthMm,
        float heightScaleMm, bool invert, float referencePlaneZ)
    {
        var bytes = File.ReadAllBytes(path);
        var img   = ImageResult.FromMemory(bytes, ColorComponents.Grey); // 1 byte/pixel
        int w = img.Width, h = img.Height;

        var samples = new float[w * h];
        for (int row = 0; row < h; row++)
        {
            int srcRow = h - 1 - row;           // image is top-left origin; ReliefMap row 0 = bottom
            for (int col = 0; col < w; col++)
                samples[row * w + col] = img.Data[srcRow * w + col] / 255f;
        }

        return new ReliefMap
        {
            Samples = samples, Cols = w, Rows = h,
            OriginX = originX, OriginY = originY, WidthMm = widthMm, LengthMm = lengthMm,
            HeightScaleMm = heightScaleMm, Invert = invert, ReferencePlaneZ = referencePlaneZ,
        };
    }
}
