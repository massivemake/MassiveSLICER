using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MassiveSlicer.Core.IO;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class RightPanelView : UserControl
{
    public RightPanelView() => InitializeComponent();

    private void JointAngle_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            e.Handled = true;
    }

    /// <summary>Double-click a preset row to load it immediately (comp — see PresetsCardViewModel.LoadSelected).</summary>
    private void OnPresetDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not RightPanelViewModel vm) return;
        if (vm.Presets.SelectedPreset is null) return;
        vm.Presets.LoadSelectedCommand.Execute(null);
    }

    /// <summary>
    /// A presets-card range filter's track owns ALL pointer interaction itself — the two handles
    /// are plain non-hit-testable Ellipses, not independent Thumbs (two earlier attempts using
    /// per-handle hit-testing both had the same failure: whichever element is topmost in z-order
    /// wins every hit-test once the handles are coincident, permanently, since the losing handle
    /// can then never be clicked again — "stuck together forever"). Pressing here picks which
    /// bound (Lower/Upper) the gesture controls — see NumericRangeFilterViewModel.DecideActiveLowerBound
    /// for the proximity/direction logic — and that choice is locked in <see cref="RangeDragState"/>
    /// for the rest of the one gesture.
    /// </summary>
    private void OnRangeTrackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        if (canvas.DataContext is not NumericRangeFilterViewModel filter) return;

        var x = e.GetPosition(canvas).X;
        var state = new RangeDragState { PressX = x, IsLower = filter.DecideActiveLowerBound(x, x) };
        canvas.Tag = state;

        e.Pointer.Capture(canvas);
        if (state.IsLower is { } isLower) filter.SetFromTrackX(isLower, x);
    }

    private void OnRangeTrackPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        if (canvas.Tag is not RangeDragState state) return;
        if (canvas.DataContext is not NumericRangeFilterViewModel filter) return;

        var x = e.GetPosition(canvas).X;
        state.IsLower ??= filter.DecideActiveLowerBound(state.PressX, x);
        if (state.IsLower is { } isLower) filter.SetFromTrackX(isLower, x);
    }

    private void OnRangeTrackPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Canvas canvas) canvas.Tag = null;
        e.Pointer.Capture(null);
    }

    /// <summary>Per-gesture state for one range-filter track — stored transiently in the
    /// Canvas.Tag while a pointer is down, cleared on release.</summary>
    private sealed class RangeDragState
    {
        public double PressX;
        public bool? IsLower;
    }

    /// <summary>Enter in the presets search box pins the current text as a removable tag.</summary>
    private void OnSearchTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not RightPanelViewModel vm) return;
        vm.Presets.CommitSearchTag();
    }

    /// <summary>
    /// Comp-only "share a preset as a file" demo — reads an ad-hoc JSON shape
    /// (see PresetsCardViewModel.ImportPresetFromJson), not the real preset file format yet.
    /// </summary>
    private async void OnImportPresetClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RightPanelViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window parent) return;

        var files = await parent.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Import Preset (comp-only format)",
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new System.IO.StreamReader(stream);
        var json = await reader.ReadToEndAsync();

        try
        {
            vm.Presets.ImportPresetFromJson(json);
        }
        catch (Exception)
        {
            vm.Presets.StatusMessage = "Could not read that file as a preset (comp-only JSON shape expected)";
        }
    }

    /// <summary>
    /// Comp-only "share a preset as a file" demo — writes the same ad-hoc JSON shape
    /// (see PresetsCardViewModel.ExportSelectedToJson), not the real preset file format yet.
    /// </summary>
    private async void OnExportPresetClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RightPanelViewModel vm) return;
        if (vm.Presets.SelectedPreset is null) return;
        if (TopLevel.GetTopLevel(this) is not Window parent) return;

        var file = await parent.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title           = "Export Preset (comp-only format)",
            SuggestedFileName = vm.Presets.SelectedPreset.Name,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream);
        await writer.WriteAsync(vm.Presets.ExportSelectedToJson());

        vm.Presets.StatusMessage = $"Exported \"{vm.Presets.SelectedPreset.Name}\" to file (comp-only format — not the real preset schema yet)";
    }

    private async void OnAddMaterialClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RightPanelViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window parent) return;

        var dialog = new MaterialPresetDialog { DataContext = new MaterialPresetEditorViewModel() };
        var result = await dialog.ShowDialog<Core.Models.MaterialPreset?>(parent);
        if (result is null) return;

        vm.Additive.MaterialPresets.Add(result);
        vm.Additive.SelectedPresetIndex = vm.Additive.MaterialPresets.Count - 1;
        MaterialPresetsLoader.Save(vm.Additive.MaterialPresets);
    }

    private async void OnKrlPostProcessClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RightPanelViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window parent) return;

        var dialog = new KrlPostProcessWindow
        {
            DataContext = vm.Additive.KrlPostProcess,
        };
        await dialog.ShowDialog(parent);
    }

    private async void OnEditMaterialClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RightPanelViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window parent) return;

        int idx = vm.Additive.SelectedPresetIndex;
        if (idx < 0 || idx >= vm.Additive.MaterialPresets.Count) return;

        var editor = new MaterialPresetEditorViewModel();
        editor.LoadFrom(vm.Additive.MaterialPresets[idx]);

        var dialog = new MaterialPresetDialog { DataContext = editor };
        var result = await dialog.ShowDialog<Core.Models.MaterialPreset?>(parent);
        if (result is null) return;

        vm.Additive.MaterialPresets[idx] = result;
        vm.Additive.SelectedPresetIndex  = idx;
        MaterialPresetsLoader.Save(vm.Additive.MaterialPresets);
    }
}
