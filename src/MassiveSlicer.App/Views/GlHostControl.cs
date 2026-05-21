using Avalonia.Controls;

namespace MassiveSlicer.App.Views;

/// <summary>
/// Platform-agnostic GL host. Uses <see cref="Win32GlHost"/> on Windows (bypasses
/// Avalonia's FBO-based <c>OpenGlControlBase</c> to avoid an AMD driver crash) and
/// falls back to <see cref="OpenGlCanvas"/> on macOS/Linux.
/// </summary>
internal sealed class GlHostControl : ContentControl
{
    public event Action? GlInitialized;
    public event Action<TimeSpan, int, int>? GlRender;
    public event Action? GlDeinitialized;

    public GlHostControl()
    {
        if (OperatingSystem.IsWindows())
        {
            var win32 = new Win32GlHost();
            win32.GlInitialized   += () => GlInitialized?.Invoke();
            win32.GlRender        += (dt, w, h) => GlRender?.Invoke(dt, w, h);
            win32.GlDeinitialized += () => GlDeinitialized?.Invoke();
            Content = win32;
        }
        else
        {
            var avl = new OpenGlCanvas();
            avl.GlInitialized   += () => GlInitialized?.Invoke();
            avl.GlRender        += (dt, w, h) => GlRender?.Invoke(dt, w, h);
            avl.GlDeinitialized += () => GlDeinitialized?.Invoke();
            Content = avl;
        }
    }

    public void RequestNextFrameRendering()
    {
        if (Content is Win32GlHost win32) win32.RequestNextFrameRendering();
        else if (Content is OpenGlCanvas avl) avl.RequestNextFrameRendering();
    }
}
