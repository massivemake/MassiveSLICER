using System.ComponentModel;
using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Backs the "5 MODIFIERS" step: add-modifier actions, plus a settings inspector for whichever
/// modifier is currently the real outliner/scene selection — or the Apply action, when the
/// selection is a whole Modifiers group instead. Stack order/membership lives entirely in the
/// outliner now (drag a modifier under a mesh to link it, drag within its Modifiers group to
/// reorder) — this panel has no list or selection state of its own to keep in sync with that.
/// </summary>
public sealed class ModifierPanelViewModel : ViewModelBase
{
    private ViewportViewModel? _viewport;

    /// <summary>Set once by MainWindowViewModel after both VMs exist.</summary>
    internal ViewportViewModel? Viewport
    {
        get => _viewport;
        set
        {
            if (ReferenceEquals(_viewport, value)) return;
            if (_viewport is not null) _viewport.PropertyChanged -= OnViewportPropertyChanged;
            _viewport = value;
            if (_viewport is not null) _viewport.PropertyChanged += OnViewportPropertyChanged;
            Refresh();
        }
    }

    private ModifierSettingsViewModel? _selectedSettings;
    /// <summary>Settings for whichever Cut modifier is currently selected, or null if the
    /// current selection isn't a modifier.</summary>
    public ModifierSettingsViewModel? SelectedSettings
    {
        get => _selectedSettings;
        private set => SetField(ref _selectedSettings, value);
    }

    private bool _isGroupSelected;
    /// <summary>True when the current selection is a whole Modifiers group — shows the Apply
    /// action instead of a single modifier's settings.</summary>
    public bool IsGroupSelected
    {
        get => _isGroupSelected;
        private set => SetField(ref _isGroupSelected, value);
    }

    /// <summary>True once a mesh is selected — gates the add-modifier buttons.</summary>
    public bool HasOwner => _viewport?.SelectedModifierOwner is not null;

    public ICommand AddCutModifierCommand { get; }
    public ICommand ApplyCommand { get; }

    public ModifierPanelViewModel()
    {
        AddCutModifierCommand = new RelayCommand(AddCutModifier, () => HasOwner);
        ApplyCommand          = new RelayCommand(Apply, () => IsGroupSelected);
    }

    private void OnViewportPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewportViewModel.SelectedOutlinerItem)
            || e.PropertyName == nameof(ViewportViewModel.SelectedModifierOwner))
            Refresh();
    }

    private void AddCutModifier()
    {
        if (_viewport?.SelectedModifierOwner is not { } owner) return;
        // Naming (a fresh "Cut NN") now happens inside AddCutModifier itself, before the node
        // and outliner row are built -- see ViewportViewModel.AddCutModifier's own doc comment.
        _viewport.AddCutModifier(owner);
        _viewport.NotifyRenderNeeded();
    }

    private void Apply()
    {
        if (_viewport?.SelectedModifierOwner is { } owner)
            _viewport.OnApplyModifiersRequested?.Invoke(owner);
    }

    private void Refresh()
    {
        var item = _viewport?.SelectedOutlinerItem;

        SelectedSettings = item?.IsModifier == true && _viewport?.FindModifierForNode(item.Node) is { } cut
            ? new ModifierSettingsViewModel(cut, _viewport)
            : null;
        IsGroupSelected = item?.IsModifiersGroup == true;

        OnPropertyChanged(nameof(HasOwner));
        (AddCutModifierCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
