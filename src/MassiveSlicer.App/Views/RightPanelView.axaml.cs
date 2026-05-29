using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
