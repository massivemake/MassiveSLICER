using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Native;

namespace MassiveSlicer.App;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        // Force desktop OpenGL (WGL) on Windows so GLSL #version 330 core shaders work.
        // Avalonia defaults to ANGLE (OpenGL ES) which rejects the 'core' profile keyword.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Wgl]
            });

        // Enable OpenGL on macOS so OpenGlControlBase can create a CGL context.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            builder = builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software]
            });

        return builder;
    }
}
