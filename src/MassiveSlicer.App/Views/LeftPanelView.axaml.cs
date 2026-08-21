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

public partial class LeftPanelView : UserControl
{
    /// <summary>Raised when the import dropzone is clicked — the window shows the file picker.</summary>
    public event Action? ImportClickRequested;

    /// <summary>Raised with local file paths dropped onto the import dropzone.</summary>
    public event Action<string[]>? ImportFilesDropped;

    /// <summary>
    /// PersistExpander restores saved open/closed state on Loaded. Don't steal the
    /// scroll position until that restore has finished and the user expands a card.
    /// </summary>
    public bool AllowExpandScroll { get; private set; }

    static LeftPanelView() => SidebarExpandScroll.Arm();

    public LeftPanelView()
    {
        InitializeComponent();

        AttachedToVisualTree += OnAttachedToVisualTree;

        ImportDropZone.PointerPressed += (_, _) => ImportClickRequested?.Invoke();
        ImportDropZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        ImportDropZone.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?
                .Select(f => f.TryGetLocalPath())
                .Where(p => p is not null)
                .Select(p => p!)
                .ToArray();
            if (paths is { Length: > 0 })
                ImportFilesDropped?.Invoke(paths);
        });
    }

    void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Dispatcher.UIThread.Post(() => AllowExpandScroll = true, DispatcherPriority.ContextIdle);
    }

    private void JointAngle_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            e.Handled = true;
    }

    // -- Material preset (moved from RightPanelView -- now lives under NOZZLE SIZE) ---------

    private async void OnAddMaterialClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window parent) return;
        if (parent.DataContext is not MainWindowViewModel main) return;
        var vm = main.RightPanel;

        // Default the calibration head to whichever extruder the active cell uses.
        var newEditor = new MaterialPresetEditorViewModel { CalibIsHf = vm.Additive.ActiveExtruderIsHf };
        var dialog = new MaterialPresetDialog { DataContext = newEditor };
        var result = await dialog.ShowDialog<Core.Models.MaterialPreset?>(parent);
        if (result is null) return;

        AddMaterialPreset(vm, result);
    }

    private async void OnEditMaterialClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window parent) return;
        if (parent.DataContext is not MainWindowViewModel main) return;
        var vm = main.RightPanel;

        int idx = vm.Additive.SelectedPresetIndex;
        if (idx < 0 || idx >= vm.Additive.MaterialPresets.Count) return;

        var editor = new MaterialPresetEditorViewModel();
        editor.LoadFrom(vm.Additive.MaterialPresets[idx]);
        // Never calibrated on this preset? Default to the active cell's head.
        if (string.IsNullOrEmpty(vm.Additive.MaterialPresets[idx].CalibratedOn))
            editor.CalibIsHf = vm.Additive.ActiveExtruderIsHf;

        var dialog = new MaterialPresetDialog { DataContext = editor };
        var result = await dialog.ShowDialog<Core.Models.MaterialPreset?>(parent);
        if (result is null)
        {
            if (dialog.DeleteRequested)
            {
                vm.Additive.MaterialPresets.RemoveAt(idx);
                vm.Additive.SelectedPresetIndex = Math.Min(idx, vm.Additive.MaterialPresets.Count - 1);
                SaveMaterialsReportingErrors(vm);
            }
            return;
        }

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

    private static void AddMaterialPreset(RightPanelViewModel vm, Core.Models.MaterialPreset preset)
    {
        vm.Additive.MaterialPresets.Add(preset);
        vm.Additive.SelectedPresetIndex = vm.Additive.MaterialPresets.Count - 1;
        SaveMaterialsReportingErrors(vm);
    }

    /// <summary>Saves the material library and surfaces a failure instead of swallowing it.</summary>
    private static void SaveMaterialsReportingErrors(RightPanelViewModel vm)
    {
        MaterialPresetsLoader.Save(vm.Additive.MaterialPresets);
        if (MaterialPresetsLoader.LastSaveError is { } err)
            vm.Presets.StatusMessage = $"⚠ Material library NOT saved: {err}";
    }
}
