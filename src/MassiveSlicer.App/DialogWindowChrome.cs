using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MassiveSlicer.App.Views;

/// <summary>
/// Shared chrome for borderless dialogs (Preferences, Material Preset, …).
///
/// Avalonia <c>TransparencyLevelHint=Transparent</c> is not enough on the
/// shop Windows build: we force WGL for the 3D viewport, and that swapchain
/// has no per-pixel alpha. The HWND stays an opaque black rectangle behind
/// the rounded <c>DialogChrome</c> card. Clip the Win32 window to the same
/// radius so those corner pixels are not part of the window.
/// </summary>
internal static class DialogWindowChrome
{
    public static void Apply(Window window)
    {
        window.WindowDecorations = WindowDecorations.None;
        window.Background = Brushes.Transparent;
        window.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        window.Opened += (_, _) =>
        {
            window.Background = Brushes.Transparent;
            window.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            ApplyWin32RoundedRegion(window);
            try
            {
                window.InvalidateVisual();
                window.InvalidateArrange();
            }
            catch
            {
                // best-effort
            }
        };

        window.SizeChanged += (_, _) => ApplyWin32RoundedRegion(window);
    }

    /// <summary>Physical pixels for <c>CreateRoundRectRgn</c> (right/bottom exclusive).</summary>
    public static (int Width, int Height, int Ellipse) PhysicalRoundRect(
        double widthDip, double heightDip, double radiusDip, double scale)
    {
        int w = Math.Max(1, (int)Math.Round(widthDip * scale));
        int h = Math.Max(1, (int)Math.Round(heightDip * scale));
        int d = Math.Max(2, (int)Math.Round(Math.Max(0, radiusDip) * 2.0 * scale));
        return (w, h, d);
    }

    static void ApplyWin32RoundedRegion(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (window.Bounds.Width < 8 || window.Bounds.Height < 8)
            return;

        IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return;

        double radius = ContentCornerRadius(window);
        double scale = window.RenderScaling;
        if (scale < 0.25) scale = 1;

        var (w, h, ellipse) = PhysicalRoundRect(
            window.Bounds.Width, window.Bounds.Height, radius, scale);

        // Win11: ask DWM to round the HWND (antialiased). WGL often ignores this.
        int pref = DwmWcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));

        // Always clip the HWND to the card radius. Those pixels stop belonging
        // to the window, so the black WGL corners disappear.
        IntPtr rgn = CreateRoundRectRgn(0, 0, w, h, ellipse, ellipse);
        if (rgn == IntPtr.Zero)
            return;
        if (SetWindowRgn(hwnd, rgn, true) == 0)
            DeleteObject(rgn);
    }

    static double ContentCornerRadius(Window window)
    {
        if (window.Content is Border border)
        {
            var r = border.CornerRadius;
            double max = Math.Max(
                Math.Max(r.TopLeft, r.TopRight),
                Math.Max(r.BottomLeft, r.BottomRight));
            if (max > 0)
                return max;
        }
        return 10;
    }

    const int DwmwaWindowCornerPreference = 33;
    const int DwmWcpRound = 2;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("user32.dll")]
    static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);
}
