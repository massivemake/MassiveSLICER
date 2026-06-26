using Avalonia.Controls;
using Avalonia.Interactivity;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.App.Views;

public partial class MeshCleanupDialog : Window
{
    public MeshCleanupDialog()
    {
        InitializeComponent();
        TitleBar.PointerPressed += (_, e) => BeginMoveDrag(e);
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MeshCleanupDialogViewModel vm) return;
        Close(vm.ToOptions());
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}