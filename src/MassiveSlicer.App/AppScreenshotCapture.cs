using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace MassiveSlicer.App;

/// <summary>Renders an Avalonia <see cref="Window"/> (full UI chrome + panels) to PNG bytes.</summary>
internal static class AppScreenshotCapture
{
    public static byte[]? CapturePng(Window window)
        => CapturePng(window, null, null);

    /// <summary>
    /// Captures the window UI and, when supplied, composites the rendered 3D viewport PNG
    /// over the region occupied by <paramref name="viewportControl"/>. On macOS/Linux the GL
    /// surface isn't captured by <see cref="RenderTargetBitmap"/> (it composites separately),
    /// so without this overlay the viewport region would be black.
    /// </summary>
    public static byte[]? CapturePng(Window window, byte[]? viewportOverlayPng, Control? viewportControl)
    {
        if (window.Bounds.Width <= 1 || window.Bounds.Height <= 1)
            return null;

        double scaling = window.RenderScaling;
        int width  = Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scaling));
        int height = Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scaling));

        var pixelSize = new PixelSize(width, height);
        var dpi       = new Vector(96 * scaling, 96 * scaling);

        using var windowRtb = new RenderTargetBitmap(pixelSize, dpi);
        windowRtb.Render(window);

        // Overlay the rendered viewport (macOS/Linux GL surface isn't captured by Render).
        // Composite into a fresh target: draw the window UI first, then the viewport image —
        // CreateDrawingContext clears, so we can't just draw the overlay onto windowRtb.
        if (viewportOverlayPng is { Length: > 0 } && viewportControl is not null)
        {
            try
            {
                using var vpStream = new MemoryStream(viewportOverlayPng);
                using var vpBmp = new Bitmap(vpStream);
                if (viewportControl.TranslatePoint(new Point(0, 0), window) is { } topLeft)
                {
                    using var combined = new RenderTargetBitmap(pixelSize, dpi);
                    using (var ctx = combined.CreateDrawingContext())
                    {
                        ctx.DrawImage(windowRtb, new Rect(windowRtb.Size));
                        var dest = new Rect(topLeft.X, topLeft.Y,
                                            viewportControl.Bounds.Width, viewportControl.Bounds.Height);
                        ctx.DrawImage(vpBmp, new Rect(vpBmp.Size), dest);
                    }
                    using var msC = new MemoryStream();
                    combined.Save(msC);
                    return msC.ToArray();
                }
            }
            catch { /* fall back to the un-composited window capture */ }
        }

        using var ms = new MemoryStream();
        windowRtb.Save(ms);
        return ms.ToArray();
    }
}
