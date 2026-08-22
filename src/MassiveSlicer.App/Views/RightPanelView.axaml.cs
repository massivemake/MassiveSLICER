using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MassiveSlicer.App.Behaviors;
using MassiveSlicer.Core.IO;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class RightPanelView : UserControl
{
    /// <summary>False until PersistExpander has restored open/closed state.</summary>
    public bool AllowExpandScroll { get; private set; }

    static RightPanelView() => SidebarExpandScroll.Arm();

    public RightPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // If DataContext was already assigned (inherited) before we subscribed.
        if (DataContext is RightPanelViewModel)
            OnDataContextChanged(this, EventArgs.Empty);

        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Console "krlpost open" must bind after we have a TopLevel.
        if (DataContext is RightPanelViewModel vm)
            vm.Additive.OnOpenKrlPostProcessRequested = () => OnKrlPostProcessClicked(this, new RoutedEventArgs());
        Dispatcher.UIThread.Post(() => AllowExpandScroll = true, DispatcherPriority.ContextIdle);
    }

    void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not RightPanelViewModel vm) return;
        vm.Subtractive.OpenBitLibraryRequested -= OnOpenBitLibraryRequested;
        vm.Subtractive.OpenBitLibraryRequested += OnOpenBitLibraryRequested;
    }

    async void OnOpenBitLibraryRequested(object? sender, EventArgs e)
    {
        try
        {
            if (DataContext is not RightPanelViewModel vm) return;
            if (TopLevel.GetTopLevel(this) is not Window parent) return;

            var libVm = new MillBitLibraryViewModel(vm.Subtractive.BitLibrary);
            var dialog = new MillBitLibraryDialog { DataContext = libVm };
            // Avoid fragile nullable-tuple ShowDialog typing — use object and cast.
            var result = await dialog.ShowDialog<object?>(parent);
            if (result is not ValueTuple<System.Collections.Generic.List<Core.Models.MillBitTool>, string?> tuple)
                return;

            vm.Subtractive.ReplaceBitLibrary(tuple.Item1, tuple.Item2);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[bits] open library failed: {ex}");
            System.Console.Error.WriteLine($"[bits] open library failed: {ex}");
        }
    }

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

        // Default the calibration head to whichever extruder the active cell uses.
        var newEditor = new MaterialPresetEditorViewModel { CalibIsHf = vm.Additive.ActiveExtruderIsHf };
        var dialog = new MaterialPresetDialog { DataContext = newEditor };
        var result = await dialog.ShowDialog<Core.Models.MaterialPreset?>(parent);
        if (result is null) return;

        AddMaterialPreset(vm, result);
    }

    /// <summary>Saves the material library and surfaces a failure instead of swallowing it.
    /// When ERP is connected, also upserts each material to lab.massivemake.com.</summary>
    private void SaveMaterialsReportingErrors(RightPanelViewModel vm)
    {
        MaterialPresetsLoader.Save(vm.Additive.MaterialPresets);
        if (MaterialPresetsLoader.LastSaveError is { } err)
            vm.Presets.StatusMessage = $"⚠ Material library NOT saved: {err}";

        // Push to ERP when the dock is connected (ViewportViewModel.Erp on the same tree).
        if (TopLevel.GetTopLevel(this) is not Window { DataContext: MainWindowViewModel main })
            return;
        if (!main.Viewport.Erp.IsConnected) return;
        foreach (var mat in vm.Additive.MaterialPresets)
            main.Viewport.Erp.PushMaterialPresetInBackground(mat);
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
        if (parent.DataContext is MainWindowViewModel main)
        {
            main.PersistSettings();
            PreferencesLoader.Save(main.AppPreferences);
        }
    }

    private async void OnEditMaterialClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RightPanelViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window parent) return;

        int idx = vm.Additive.SelectedPresetIndex;
        if (idx < 0 || idx >= vm.Additive.MaterialPresets.Count) return;

        var editor = new MaterialPresetEditorViewModel();
        editor.LoadFrom(vm.Additive.MaterialPresets[idx]);
        // Never calibrated on this preset? Default to the active cell's head.
        if (string.IsNullOrEmpty(vm.Additive.MaterialPresets[idx].CalibratedOn))
            editor.CalibIsHf = vm.Additive.ActiveExtruderIsHf;

        var dialog = new MaterialPresetDialog { DataContext = editor };
        var result = await dialog.ShowDialog<Core.Models.MaterialPreset?>(parent);
        if (result is null) return;

        // JSON import marks SaveAsNew so the open preset is left alone and a new entry is added.
        if (dialog.SaveAsNew)
        {
            AddMaterialPreset(vm, result);
            return;
        }

        vm.Additive.MaterialPresets[idx] = result;
        vm.Additive.SelectedPresetIndex  = idx;
        SaveMaterialsReportingErrors(vm);
    }

    private void AddMaterialPreset(RightPanelViewModel vm, Core.Models.MaterialPreset preset)
    {
        vm.Additive.MaterialPresets.Add(preset);
        vm.Additive.SelectedPresetIndex = vm.Additive.MaterialPresets.Count - 1;
        SaveMaterialsReportingErrors(vm);
    }
}
