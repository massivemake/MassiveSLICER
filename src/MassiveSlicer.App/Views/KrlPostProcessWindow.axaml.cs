using Avalonia.Controls;
using Avalonia.Interactivity;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class KrlPostProcessWindow : Window
{
    public KrlPostProcessWindow()
    {
        InitializeComponent();
        TitleBar.PointerPressed += (_, e) => BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (DataContext is KrlPostProcessSettingsViewModel vm)
            vm.Save();
        Close();
    }
}