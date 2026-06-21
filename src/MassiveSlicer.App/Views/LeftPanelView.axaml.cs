using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class LeftPanelView : UserControl
{
    public LeftPanelView() => InitializeComponent();

    private void JointAngle_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            e.Handled = true;
    }

    private void OnOutlinerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not OutlinerItemViewModel item) return;
        RequestViewportSelect(item.Node);
    }

    private void OnOutlinerChildPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed != true) return;
        if (sender is not Control { DataContext: OutlinerItemViewModel item }) return;
        RequestViewportSelect(item.Node);
        e.Handled = true;
    }

    private void RequestViewportSelect(MassiveSlicer.Viewport.Scene.SceneNode node)
    {
        if (TopLevel.GetTopLevel(this) is Window { DataContext: MainWindowViewModel mvm })
            mvm.Viewport.OnOutlinerSelectRequested?.Invoke(node);
    }
}
