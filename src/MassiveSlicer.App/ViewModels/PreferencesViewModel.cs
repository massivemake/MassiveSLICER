using System.Windows.Input;
using Avalonia.Media;
using MassiveSlicer.App;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Backs the Preferences window. Every property setter writes through to the
/// shared <see cref="AppPreferences"/> instance, invokes a sync callback so the
/// live viewport reflects the change immediately, and saves to disk.
/// No Apply/Cancel step is needed.
/// </summary>
public sealed class PreferencesViewModel : ViewModelBase
{
    private readonly AppPreferences _prefs;
    private readonly Action         _onChanged;
    private bool                    _loading;

    // -- Navigation --------------------------------------------------------

    private bool _autoDepth;
    public bool AutoDepth
    {
        get => _autoDepth;
        set { if (SetField(ref _autoDepth, value)) Commit(() => _prefs.AutoDepth = value); }
    }

    private bool _orbitAroundSelection;
    public bool OrbitAroundSelection
    {
        get => _orbitAroundSelection;
        set { if (SetField(ref _orbitAroundSelection, value)) Commit(() => _prefs.OrbitAroundSelection = value); }
    }

    private NavigationPresetId _activePreset;
    public NavigationPresetId ActivePreset
    {
        get => _activePreset;
        set { if (SetField(ref _activePreset, value)) Commit(() => _prefs.ActivePreset = value); }
    }

    /// <summary>All available navigation presets for display in the list.</summary>
    public IReadOnlyList<NavigationPreset> NavigationPresets { get; } = NavigationPreset.All;

    // -- Performance -------------------------------------------------------

    private bool _antiAliasing;
    public bool AntiAliasing
    {
        get => _antiAliasing;
        set { if (SetField(ref _antiAliasing, value)) Commit(() => _prefs.AntiAliasing = value); }
    }

    // -- Appearance --------------------------------------------------------

    private AppTheme _activeTheme;
    public AppTheme ActiveTheme
    {
        get => _activeTheme;
        set { if (SetField(ref _activeTheme, value)) Commit(() => _prefs.ActiveTheme = value.ToString()); }
    }

    /// <summary>All available themes for the dropdown.</summary>
    public IReadOnlyList<AppTheme> AvailableThemes { get; } = Enum.GetValues<AppTheme>();

    // -- Toolpath colors -------------------------------------------------------

    private Color _toolpathExtrudeColor;
    public Color ToolpathExtrudeColor
    {
        get => _toolpathExtrudeColor;
        set { if (SetField(ref _toolpathExtrudeColor, value)) Commit(() => _prefs.ToolpathExtrudeColor = ColorToHex(value)); }
    }

    private Color _toolpathTravelColor;
    public Color ToolpathTravelColor
    {
        get => _toolpathTravelColor;
        set { if (SetField(ref _toolpathTravelColor, value)) Commit(() => _prefs.ToolpathTravelColor = ColorToHex(value)); }
    }

    private Color _toolpathSeamColor;
    public Color ToolpathSeamColor
    {
        get => _toolpathSeamColor;
        set { if (SetField(ref _toolpathSeamColor, value)) Commit(() => _prefs.ToolpathSeamColor = ColorToHex(value)); }
    }

    private Color _toolpathUnselectedColor;
    public Color ToolpathUnselectedColor
    {
        get => _toolpathUnselectedColor;
        set { if (SetField(ref _toolpathUnselectedColor, value)) Commit(() => _prefs.ToolpathUnselectedColor = ColorToHex(value)); }
    }

    private static string ColorToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color HexToColor(string hex)
    {
        try
        {
            var s = hex.TrimStart('#');
            if (s.Length == 6) s = "FF" + s;
            return new Color(
                Convert.ToByte(s[0..2], 16),
                Convert.ToByte(s[2..4], 16),
                Convert.ToByte(s[4..6], 16),
                Convert.ToByte(s[6..8], 16));
        }
        catch { return Colors.White; }
    }

    // -- Commands ----------------------------------------------------------

    /// <summary>Selects a navigation preset by its ID.</summary>
    public ICommand SelectPresetCommand { get; }

    // -- Lifecycle ---------------------------------------------------------

    /// <summary>
    /// Initialises the ViewModel.
    /// </summary>
    /// <param name="prefs">Shared preferences instance to write through to.</param>
    /// <param name="onChanged">
    /// Called after each committed change so the viewport (and other subsystems)
    /// can sync immediately without waiting for a dialog to close.
    /// </param>
    public PreferencesViewModel(AppPreferences prefs, Action onChanged)
    {
        _prefs     = prefs;
        _onChanged = onChanged;
        SelectPresetCommand = new RelayCommand<NavigationPresetId>(id => ActivePreset = id);
        LoadFromPrefs();
    }

    /// <summary>Populates UI-bound fields from the underlying preferences object.</summary>
    public void LoadFromPrefs()
    {
        _loading                = true;
        AutoDepth               = _prefs.AutoDepth;
        OrbitAroundSelection    = _prefs.OrbitAroundSelection;
        ActivePreset            = _prefs.ActivePreset;
        AntiAliasing            = _prefs.AntiAliasing;
        ActiveTheme             = Enum.TryParse<AppTheme>(_prefs.ActiveTheme, out var t) ? t : AppTheme.Obsidian;
        ToolpathExtrudeColor    = HexToColor(_prefs.ToolpathExtrudeColor);
        ToolpathTravelColor     = HexToColor(_prefs.ToolpathTravelColor);
        ToolpathSeamColor       = HexToColor(_prefs.ToolpathSeamColor);
        ToolpathUnselectedColor = HexToColor(_prefs.ToolpathUnselectedColor);
        _loading                = false;
    }

    // -- Private -----------------------------------------------------------

    private void Commit(Action writeToPrefs)
    {
        if (_loading) return;
        writeToPrefs();
        _onChanged();
        PreferencesLoader.Save(_prefs);
    }
}

/// <summary>Section tabs in the Preferences window.</summary>
public enum PrefsSection
{
    Navigation,
    Performance,
    Appearance,
}
