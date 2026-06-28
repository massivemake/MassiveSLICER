using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class OutlinerItemView : UserControl
{
    public OutlinerItemView()
    {
        InitializeComponent();
        RowContextMenu.Opening += OnContextMenuOpening;
    }

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not OutlinerItemViewModel item) return;
        var point = e.GetCurrentPoint(sender as Control);
        if (TopLevel.GetTopLevel(this) is not Window { DataContext: MainWindowViewModel mvm })
            return;

        if (point.Properties.IsLeftButtonPressed)
        {
            mvm.Viewport.SuppressNextOutlinerListBoxSelection = true;
            mvm.Viewport.OnOutlinerSelectRequested?.Invoke(item.Node);
            e.Handled = true;
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            mvm.Viewport.SuppressNextOutlinerListBoxSelection = true;
            mvm.Viewport.OnOutlinerSelectRequested?.Invoke(item.Node);
        }
    }

    private void OnRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (DataContext is not OutlinerItemViewModel item) return;
        if (TopLevel.GetTopLevel(this) is not Window { DataContext: MainWindowViewModel mvm })
            return;

        mvm.Viewport.SuppressNextOutlinerListBoxSelection = true;
        mvm.Viewport.OnOutlinerSelectRequested?.Invoke(item.Node);
    }

    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (DataContext is OutlinerItemViewModel item)
            item.RefreshModelCommands();
    }
}