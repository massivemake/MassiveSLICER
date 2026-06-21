using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class BottomLeftDockView : UserControl
{
    private bool  _resizing;
    private double _resizeStartHeight;
    private double _resizeStartY;

    public BottomLeftDockView()
    {
        InitializeComponent();
        ResizeGrip.PointerPressed  += ResizeGrip_PointerPressed;
        ResizeGrip.PointerMoved    += ResizeGrip_PointerMoved;
        ResizeGrip.PointerReleased += ResizeGrip_PointerReleased;
    }

    private ToolbarViewModel? Toolbar =>
        DataContext is MainWindowViewModel root ? root.Toolbar : null;

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Toolbar is not { IsConsoleVisible: true } toolbar) return;
        if (!e.GetCurrentPoint(ResizeGrip).Properties.IsLeftButtonPressed) return;

        _resizeStartHeight = toolbar.ConsolePanelHeight;
        _resizeStartY      = e.GetPosition(this).Y;
        _resizing          = true;
        e.Pointer.Capture(ResizeGrip);
        e.Handled         = true;
    }

    private void ResizeGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing || Toolbar is not { } toolbar) return;

        var y     = e.GetPosition(this).Y;
        var delta = _resizeStartY - y;
        toolbar.ConsolePanelHeight = _resizeStartHeight + delta;
    }

    private void ResizeGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);
    }
}