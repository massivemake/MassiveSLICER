using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace MassiveSlicer.App;

public partial class App : Application
{
    /// <summary>Workspace path from a double-clicked <c>.mass</c> file or drag onto the exe.</summary>
    internal static string? StartupWorkspacePath { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // UI-thread exceptions otherwise abort with a symbol-less SIGABRT on macOS.
        // Log the managed exception first so we have a useful trail in /tmp.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "massiveslicer-crash.log");
                File.AppendAllText(path,
                    $"[{DateTime.Now:O}] Dispatcher.UIThread.UnhandledException\n{e.Exception}\n\n");
                global::System.Console.Error.WriteLine($"[crash] UI exception: {e.Exception}");
            }
            catch
            {
                // best-effort logging
            }
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SliderTypeIn.Install();
            MacDockIcon.TrySet("macos-app-icon.png");
            StartupWorkspacePath = ResolveStartupWorkspacePath(desktop.Args);
            desktop.MainWindow = new MainWindow();
            desktop.ShutdownRequested += (_, _) => MassiveSlicer.Core.Scanning.ZividScanService.Disconnect();
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static string? ResolveStartupWorkspacePath(string[]? args)
    {
        if (args is not { Length: > 0 }) return null;

        foreach (var raw in args)
        {
            var path = raw.Trim().Trim('"');
            if (path.Length == 0) continue;
            if (!path.EndsWith(".mass", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full))
                    return full;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return null;
    }

    /// <summary>
    /// Swaps the active theme by replacing the first MergedDictionary entry.
    /// DynamicResource bindings throughout the app update automatically.
    /// Also syncs SimpleTheme's ThemeDictionaries accent keys so that controls
    /// like the Slider thumb (which use ThemeAccentBrush internally) follow the theme.
    /// </summary>
    public void ApplyTheme(AppTheme theme)
    {
        var uri     = new Uri($"avares://MassiveSlicer.App/Resources/Themes/{theme}.axaml");
        var include = new ResourceInclude(new Uri("avares://MassiveSlicer.App/")) { Source = uri };
        var merged  = Resources.MergedDictionaries;
        if (merged.Count > 0)
            merged[0] = include;
        else
            merged.Add(include);

        SyncThemeAccentKeys(include);
    }

    // SimpleTheme resolves ALL its built-in control colours (Slider track, ScrollBar,
    // CheckBox, TextBox borders, focus rings …) from ThemeDictionaries keys that are
    // hardcoded to Obsidian values in App.axaml and are NOT touched by the
    // MergedDictionaries swap above.  This method reads every relevant brush from the
    // newly-loaded theme file and writes it into the Dark ThemeDictionary so every
    // SimpleTheme control updates automatically — no CSS template selectors needed.
    private void SyncThemeAccentKeys(IResourceProvider src)
    {
        if (!Resources.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out var variant) ||
            variant is not ResourceDictionary rd)
            return;

        // Helper: pull a brush from the theme file (returns null if missing).
        SolidColorBrush? B(string key)
            => src.TryGetResource(key, null, out var v) && v is SolidColorBrush b ? b : null;

        // Helper: write both the Color and Brush variants for a SimpleTheme base key
        // (e.g. "ThemeAccent" → ThemeAccentColor + ThemeAccentBrush).
        void Sync(string baseName, SolidColorBrush? brush)
        {
            if (brush is null) return;
            rd[baseName + "Color"]  = brush.Color;
            rd[baseName + "Brush"]  = new SolidColorBrush(brush.Color);
        }

        // ── Accent / highlight ───────────────────────────────────────────────────
        Sync("ThemeAccent",               B("Accent"));
        if (B("Accent") is { } accent)
        {
            var c = accent.Color;
            void SyncAlpha(string key, byte a)
            {
                var col = Avalonia.Media.Color.FromArgb(a, c.R, c.G, c.B);
                rd[key + "Color"] = col;
                rd[key + "Brush"] = new SolidColorBrush(col);
            }
            SyncAlpha("ThemeAccent2", 0xCC);
            SyncAlpha("ThemeAccent3", 0x99);
            SyncAlpha("ThemeAccent4", 0x66);
        }
        Sync("ThemeControlHighlightHigh", B("AccentHover"));
        Sync("ThemeControlHighlightMid",  B("Accent"));
        Sync("ThemeControlHighlightLow",  B("AccentMuted"));
        Sync("Highlight",                 B("Accent"));
        Sync("HighlightForeground",       B("TextPrimary"));

        // ── Neutral surfaces — slider track, scrollbar channel/thumb, borders ───
        Sync("ThemeBackground",      B("Bg1"));
        Sync("ThemeForeground",      B("TextPrimary"));
        Sync("ThemeForegroundLow",   B("TextSecondary"));
        Sync("ThemeBorderHigh",      B("Border2"));
        Sync("ThemeBorderMid",       B("Border1"));
        Sync("ThemeBorderLow",       B("Border0"));
        Sync("ThemeControlLow",      B("Bg2"));
        Sync("ThemeControlMid",      B("Bg3"));
        Sync("ThemeControlMidHigh",  B("Bg3"));
        Sync("ThemeControlHigh",     B("Bg4"));
        Sync("ThemeControlVeryHigh", B("Bg4"));
    }
}
