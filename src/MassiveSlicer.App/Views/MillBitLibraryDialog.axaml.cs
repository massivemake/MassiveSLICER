using Avalonia.Controls;
using Avalonia.Interactivity;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class MillBitLibraryDialog : Window
{
    public MillBitLibraryDialog()
    {
        InitializeComponent();
        DialogWindowChrome.Apply(this);
        TitleBar.PointerPressed += (_, e) => BeginMoveDrag(e);
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close(null);

    void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MillBitLibraryViewModel vm) return;
        Close((vm.Snapshot(), vm.SelectedTool?.Id));
    }
}
