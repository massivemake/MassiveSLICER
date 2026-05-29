using System.Windows.Input;
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
        _loading             = true;
        AutoDepth            = _prefs.AutoDepth;
        OrbitAroundSelection = _prefs.OrbitAroundSelection;
        ActivePreset         = _prefs.ActivePreset;
        AntiAliasing         = _prefs.AntiAliasing;
        _loading             = false;
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
}
