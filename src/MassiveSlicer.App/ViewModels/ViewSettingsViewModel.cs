using MassiveSlicer.Viewport;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Controls viewport appearance: active theme, shader mode, environment map,
/// shadow catchers, and individual scene lights.
/// </summary>
public sealed class ViewSettingsViewModel : ViewModelBase
{
    private AppTheme _activeTheme = AppTheme.Obsidian;

    /// <summary>The active UI colour theme applied to the entire application.</summary>
    public AppTheme ActiveTheme
    {
        get => _activeTheme;
        set => SetField(ref _activeTheme, value);
    }

    private ShaderMode _shaderMode = ShaderMode.Standard;

    /// <summary>The active viewport shading/matcap mode.</summary>
    public ShaderMode ShaderMode
    {
        get => _shaderMode;
        set => SetField(ref _shaderMode, value);
    }

    private bool _showEdges;

    /// <summary>Whether mesh edges are drawn over the shaded surface.</summary>
    public bool ShowEdges
    {
        get => _showEdges;
        set => SetField(ref _showEdges, value);
    }

    private bool _shadowCatcherEnabled;

    /// <summary>Whether the ground-plane shadow catcher is active.</summary>
    public bool ShadowCatcherEnabled
    {
        get => _shadowCatcherEnabled;
        set => SetField(ref _shadowCatcherEnabled, value);
    }

    /// <summary>All available themes, for binding to the theme swatch buttons.</summary>
    public IReadOnlyList<AppTheme> AvailableThemes { get; } = Enum.GetValues<AppTheme>();
}
