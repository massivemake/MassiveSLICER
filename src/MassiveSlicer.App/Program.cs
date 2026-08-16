using System.Net.Http;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Native;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;

namespace MassiveSlicer.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Capture unhandled managed exceptions so UI crashes leave a trail
        // (macOS crash reports only show native frames for Avalonia/dotnet).
        string crashLog = Path.Combine(Path.GetTempPath(), "massiveslicer-crash.log");
        void LogCrash(string source, Exception? ex)
        {
            try
            {
                var msg = $"[{DateTime.Now:O}] {source}\n{ex}\n\n";
                File.AppendAllText(crashLog, msg);
                global::System.Console.Error.WriteLine(msg);
            }
            catch { /* best effort */ }
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // Cell JSON + GLBs resolve from assets/ next to the executable.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        ProtocolRegistration.Ensure();
        if (TryHandoffToRunningInstance(args))
            return;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash("Main.StartWithClassicDesktopLifetime", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        IconProvider.Current.Register<MaterialDesignIconProvider>();

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

        // On macOS, request CGL (native OpenGL) so OpenGlControlBase gets a desktop GL
        // context rather than Metal or Software. This is required for GLSL #version 330
        // core shaders and for the FBO blit path in GlHostControl.NonWindows.cs.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            builder = builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software]
            });

        return builder;
    }

    /// <summary>
    /// If MassiveSLICER is already open, send the .mass path to the localhost
    /// bridge and exit so Windows does not start a second instance.
    /// </summary>
    static bool TryHandoffToRunningInstance(string[] args)
    {
        string? path = MassiveSlicer.Core.IO.ProtocolUri.ResolveWorkspacePath(args);
        if (string.IsNullOrEmpty(path)) return false;
        string json = System.Text.Json.JsonSerializer.Serialize(new { path });
        for (int port = 8723; port <= 8728; port++)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var resp = http.PostAsync($"http://127.0.0.1:{port}/open", content).GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) return true;
            }
            catch
            {
                // nothing listening on this port
            }
        }
        return false;
    }
}
