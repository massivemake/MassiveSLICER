using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MassiveSlicer.App.Views;

public partial class ConsoleView : UserControl
{
    private Point _dragOffset;
    private bool  _dragging;

    public ConsoleView()
    {
        InitializeComponent();
        TitleBar.PointerPressed  += TitleBar_PointerPressed;
        TitleBar.PointerMoved    += TitleBar_PointerMoved;
        TitleBar.PointerReleased += TitleBar_PointerReleased;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TitleBar).Properties.IsLeftButtonPressed) return;
        var parent = Parent as Visual;
        if (parent == null) return;
        var pos  = e.GetPosition(parent);
        double left = Canvas.GetLeft(this);
        double top  = Canvas.GetTop(this);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top))  top  = 0;
        _dragOffset = new Point(pos.X - left, pos.Y - top);
        _dragging = true;
        e.Pointer.Capture(TitleBar);
        e.Handled = true;
    }

    private void TitleBar_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var parent = Parent as Visual;
        if (parent == null) return;
        var pos = e.GetPosition(parent);
        Canvas.SetLeft(this, pos.X - _dragOffset.X);
        Canvas.SetTop(this,  pos.Y - _dragOffset.Y);
    }

    private void TitleBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }
}
