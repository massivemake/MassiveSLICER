using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class CutToolDialog : Window
{
    public CutToolDialog()
    {
        InitializeComponent();
        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CutToolDialogViewModel vm)
            Close(vm);
        else
            Close(null);
    }

    private void OnNormalX(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CutToolDialogViewModel vm) return;
        vm.NormalX = 1; vm.NormalY = 0; vm.NormalZ = 0;
    }

    private void OnNormalY(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CutToolDialogViewModel vm) return;
        vm.NormalX = 0; vm.NormalY = 1; vm.NormalZ = 0;
    }

    private void OnNormalZ(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CutToolDialogViewModel vm) return;
        vm.NormalX = 0; vm.NormalY = 0; vm.NormalZ = 1;
    }
}
