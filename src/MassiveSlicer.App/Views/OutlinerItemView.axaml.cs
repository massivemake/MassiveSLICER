using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MassiveSlicer.App;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

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

        var shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var ctrlHeld  = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (point.Properties.IsLeftButtonPressed)
        {
            if (mvm.Viewport.IsToolheadGroupItem(item))
                return;

            if (OutlinerModelOps.IsToolheadItem(item))
            {
                mvm.Viewport.ClearScanOutlinerSelection();
                if (mvm.Viewport.TryGetToolheadName(item, out var toolName))
                    mvm.Viewport.OnOutlinerToolheadSelected?.Invoke(toolName);
                e.Handled = true;
                return;
            }

            if (OutlinerModelOps.IsScanItem(item))
            {
                mvm.Viewport.OnOutlinerScanClicked(item, shiftHeld, ctrlHeld);
                // Ctrl/shift multi-select must not run the single-select viewport sync path.
                if (!shiftHeld && !ctrlHeld)
                    mvm.Viewport.OnOutlinerSelectRequested?.Invoke(item.Node);
                else
                    mvm.Viewport.OnOutlinerMultiScanViewportSync?.Invoke(item.Node);
            }
            else
            {
                mvm.Viewport.ClearScanOutlinerSelection();
                mvm.Viewport.OnOutlinerSelectRequested?.Invoke(item.Node);
            }

            e.Handled = true;
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            if (OutlinerModelOps.IsScanItem(item))
            {
                // Preserve multi-select when opening the merge context menu.
                if (mvm.Viewport.SelectedScanCount < 2 && !item.IsScanMultiSelected)
                    mvm.Viewport.OnOutlinerScanClicked(item, shiftHeld: false, ctrlHeld: false);

                if (mvm.Viewport.SelectedScanCount < 2)
                    mvm.Viewport.OnOutlinerSelectRequested?.Invoke(item.Node);
            }
            else
                mvm.Viewport.OnOutlinerSelectRequested?.Invoke(item.Node);
        }
    }

    private void OnRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (DataContext is not OutlinerItemViewModel item) return;
        if (TopLevel.GetTopLevel(this) is not Window { DataContext: MainWindowViewModel mvm })
            return;

        // Context-menu release must not collapse scan multi-select back to a single row.
        if (OutlinerModelOps.IsScanItem(item))
            return;

        mvm.Viewport.OnOutlinerSelectRequested?.Invoke(item.Node);
    }

    private void OnMergePointCloudClick(object? sender, RoutedEventArgs e)
        => RequestMergeFromContextMenu(ScanMergeOutput.PointCloud);

    private void OnMergeMeshClick(object? sender, RoutedEventArgs e)
        => RequestMergeFromContextMenu(ScanMergeOutput.Mesh);

    private void OnExportPointCloudClick(object? sender, RoutedEventArgs e)
        => RequestScanExport(pointCloud: true);

    private void OnExportScanMeshClick(object? sender, RoutedEventArgs e)
        => RequestScanExport(pointCloud: false);

    void RequestScanExport(bool pointCloud)
    {
        if (DataContext is not OutlinerItemViewModel item) return;
        if (TopLevel.GetTopLevel(this) is not Window { DataContext: MainWindowViewModel mvm })
            return;
        if (!OutlinerModelOps.IsScanItem(item)) return;

        if (pointCloud)
            _ = mvm.Viewport.OnExportScanPointCloudRequested?.Invoke(item.Node);
        else
            _ = mvm.Viewport.OnExportScanMeshRequested?.Invoke(item.Node);
    }

    void RequestMergeFromContextMenu(ScanMergeOutput output)
    {
        if (TopLevel.GetTopLevel(this) is not Window { DataContext: MainWindowViewModel mvm })
            return;

        mvm.Viewport.RequestMergeScans(output);
    }

    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window { DataContext: MainWindowViewModel mvm })
            return;

        bool hasGeometry = false;
        bool hasTriangles = false;
        if (DataContext is OutlinerItemViewModel row && OutlinerModelOps.IsScanItem(row))
        {
            row.RefreshModelCommands();
            hasGeometry  = OutlinerModelOps.HasMeshGeometry(row.Node);
            hasTriangles = ScanGeometryExporter.HasTriangleMesh(row.Node);
        }

        mvm.Viewport.MergeScansAsPointCloudCommand.RaiseCanExecuteChanged();
        mvm.Viewport.MergeScansAsMeshCommand.RaiseCanExecuteChanged();

        foreach (var child in RowContextMenu.Items)
        {
            if (child is not MenuItem menuItem) continue;
            var header = menuItem.Header?.ToString() ?? "";
            if (header.StartsWith("Merge as", StringComparison.Ordinal))
            {
                menuItem.IsVisible = mvm.Viewport.CanMergeScans;
                menuItem.IsEnabled = mvm.Viewport.CanMergeScans;
            }
            else if (header.StartsWith("Export Point Cloud", StringComparison.Ordinal))
            {
                menuItem.IsVisible = hasGeometry;
                menuItem.IsEnabled = hasGeometry;
            }
            else if (header.StartsWith("Export Mesh", StringComparison.Ordinal))
            {
                menuItem.IsVisible = hasTriangles;
                menuItem.IsEnabled = hasTriangles;
            }
        }
    }
}