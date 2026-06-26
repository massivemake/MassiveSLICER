using Avalonia.Controls;
using Avalonia.Input;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class OutlinerItemView : UserControl
{
    public OutlinerItemView() => InitializeComponent();

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed != true) return;
        if (DataContext is not OutlinerItemViewModel item) return;
        if (TopLevel.GetTopLevel(this) is Window { DataContext: MainWindowViewModel mvm })
        {
            mvm.Viewport.SuppressNextOutlinerListBoxSelection = true;
            mvm.Viewport.OnOutlinerSelectRequested?.Invoke(item.Node);
        }
        e.Handled = true;
    }
}