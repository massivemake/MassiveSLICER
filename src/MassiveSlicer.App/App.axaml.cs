using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace MassiveSlicer.App;

public partial class App : Application
{
    /// <summary>What to open at launch: a double-clicked <c>.mass</c>, a drag onto the exe, or an
    /// "Open in MassiveSLICER" link (a .mass, or a model + saveTo). MainWindow handles it.</summary>
    internal static MassiveSlicer.Core.IO.OpenLink? StartupLink { get; private set; }

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
            NumericFieldUx.Install();
            LengthFieldUx.Install();
            MacDockIcon.TrySet("macos-app-icon.png");
            StartupLink = ResolveStartupLink(desktop.Args);
            // macOS delivers massiveslicer:// links and Finder double-clicks as events after
            // launch, never as argv — on cold start and when the app is already open.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                activatable.Activated += OnActivated;
            desktop.MainWindow = new MainWindow();
            desktop.ShutdownRequested += (_, _) => MassiveSlicer.Core.Scanning.ZividScanService.Disconnect();
        }

        base.OnFrameworkInitializationCompleted();
    }

    void OnActivated(object? sender, ActivatedEventArgs e)
    {
        string? raw = e switch
        {
            ProtocolActivatedEventArgs p => p.Uri.OriginalString,
            FileActivatedEventArgs f => f.Files.Select(x => x.TryGetLocalPath()).FirstOrDefault(x => x is not null),
            _ => null,
        };
        if (raw is null) return;

        var link = ResolveStartupLink([raw]);
        if (link is null)
        {
            // macOS also reports the app's own .dll as an opened file at launch — not worth a line.
            if (e is ProtocolActivatedEventArgs)
                global::System.Console.Error.WriteLine($"[open] Ignored link (not a .mass/model on MassiveFILES): {raw}");
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow?.DataContext is MassiveSlicer.ViewModels.MainWindowViewModel { OpenLinkRequested: { } open })
                _ = open(link);
            else
                StartupLink = link; // window not built yet — MainWindow handles StartupLink when it is
        });
    }

    internal static MassiveSlicer.Core.IO.OpenLink? ResolveStartupLink(string[]? args)
    {
        var link = MassiveSlicer.Core.IO.OpenLink.FromArgs(
            args, MassiveSlicer.Core.IO.UnasPaths.LocalShareRoots(), File.Exists);
        if (link is null || link.SaveTo is not null) return link;
        return NormalizeMassPath(link.Path) is { } full ? link with { Path = full } : null;
    }

    internal static string? ResolveStartupWorkspacePath(string[]? args)
        => ResolveStartupLink(args) is { IsModel: false } link ? link.Path : null;

    static string? NormalizeMassPath(string resolved)
    {
        if (string.IsNullOrEmpty(resolved)) return null;
        try
        {
            var full = Path.GetFullPath(resolved);
            if (File.Exists(full))
                return full;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (NotSupportedException) { }
        return File.Exists(resolved) ? resolved : resolved.Length > 0 ? resolved : null;
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

        // Windows system accent (ComboBox/TextBox focus rings) — never leave OS purple
        if (B("Accent") is { } sysAccent)
        {
            var sc = sysAccent.Color;
            rd["SystemAccentColor"] = sc;
            rd["SystemAccentColorDark1"] = sc;
            rd["SystemAccentColorDark2"] = B("AccentMuted")?.Color ?? sc;
            rd["SystemAccentColorDark3"] = Avalonia.Media.Color.FromRgb(
                (byte)(sc.R / 2), (byte)(sc.G / 2), (byte)(sc.B / 2));
            rd["SystemAccentColorLight1"] = B("AccentHover")?.Color ?? sc;
            rd["SystemAccentBrush"] = new SolidColorBrush(sc);
            rd["SystemControlHighlightAccentBrush"] = new SolidColorBrush(sc);
        }

        // ── Neutral surfaces — slider track, scrollbar channel/thumb, borders ───
        Sync("ThemeBackground",      B("Bg1"));
        Sync("ThemeForeground",      B("TextPrimary"));
        Sync("ThemeForegroundLow",   B("TextSecondary"));
        // Hover/focus field ring — Accent lime, never Border2 (Cosmic was purple).
        Sync("ThemeBorderHigh",      B("Accent"));
        // Also write the always-on (non-variant) keys so SimpleTheme Default lookup is lime.
        if (B("Accent") is { } borderAccent)
        {
            Resources["ThemeBorderHighBrush"] = new SolidColorBrush(borderAccent.Color);
            Resources["ThemeBorderHighColor"] = borderAccent.Color;
            Resources["ThemeAccentBrush"] = new SolidColorBrush(borderAccent.Color);
            Resources["ThemeAccentColor"] = borderAccent.Color;
        }
        Sync("ThemeBorderMid",       B("Border1"));
        Sync("ThemeBorderLow",       B("Border0"));
        Sync("ThemeControlLow",      B("Bg2"));
        Sync("ThemeControlMid",      B("Bg3"));
        Sync("ThemeControlMidHigh",  B("Bg3"));
        Sync("ThemeControlHigh",     B("Bg4"));
        Sync("ThemeControlVeryHigh", B("Bg4"));
    }
}
