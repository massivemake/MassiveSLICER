using Avalonia.Controls;
using Avalonia.Interactivity;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class MaterialPresetDialog : Window
{
    public MaterialPresetDialog()
    {
        InitializeComponent();
        TitleBar.PointerPressed += (_, e) => BeginMoveDrag(e);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MaterialPresetEditorViewModel vm) return;
        Close(vm.ToPreset());
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
