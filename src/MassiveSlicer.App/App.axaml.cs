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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
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
