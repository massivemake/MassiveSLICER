using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace MassiveSlicer.App;

/// <summary>Renders an Avalonia <see cref="Window"/> (full UI chrome + panels) to PNG bytes.</summary>
internal static class AppScreenshotCapture
{
    public static byte[]? CapturePng(Window window)
    {
        if (window.Bounds.Width <= 1 || window.Bounds.Height <= 1)
            return null;

        double scaling = window.RenderScaling;
        int width  = Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scaling));
        int height = Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scaling));

        using var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * scaling, 96 * scaling));
        rtb.Render(window);

        using var ms = new MemoryStream();
        rtb.Save(ms);
        return ms.ToArray();
    }
}