using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class BottomLeftDockView : UserControl
{
    private void OnGoToValidationIssue(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MassiveSlicer.ViewModels.MainWindowViewModel m)
            m.Viewport.JumpToValidationIssue();
    }

    private bool  _resizing;
    private double _resizeStartHeight;
    private double _resizeStartY;
    private readonly DispatcherTimer _pathSpin;
    private readonly Avalonia.Media.RotateTransform _pathSpinTransform;
    private double _pathSpinAngle;
    private ViewportViewModel? _pathSpinVm;

    private void OnValidationLineTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control host) return;
        if (DataContext is not MainWindowViewModel m) return;
        if (!m.Viewport.ShowValidationLine) return;

        var panel = new StackPanel { Spacing = 8, MaxWidth = 460 };
        var detail = new TextBlock
        {
            Text = m.Viewport.ValidationDetail,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 460,
        };
        if (m.Viewport.ValidationLineIsAlert)
            detail.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFFF7070"));
        panel.Children.Add(detail);
        if (m.Viewport.HasValidationIssueJump)
        {
            var jump = new Button
            {
                Content = "Go to issue",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            jump.Click += OnGoToValidationIssue;
            panel.Children.Add(jump);
        }

        new Flyout
        {
            Content = panel,
            Placement = PlacementMode.TopEdgeAlignedRight,
        }.ShowAt(host);
        e.Handled = true;
    }

    public BottomLeftDockView()
    {
        InitializeComponent();
        _pathSpinTransform = new Avalonia.Media.RotateTransform();
        PathSpinArc.RenderTransform = _pathSpinTransform;
        _pathSpin = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _pathSpin.Tick += (_, _) =>
        {
            _pathSpinAngle = (_pathSpinAngle + 8) % 360;
            _pathSpinTransform.Angle = _pathSpinAngle;
        };
        DataContextChanged += (_, _) => HookPathSpin();
        HookPathSpin();
        ResizeGrip.PointerPressed  += ResizeGrip_PointerPressed;
        ResizeGrip.PointerMoved    += ResizeGrip_PointerMoved;
        ResizeGrip.PointerReleased += ResizeGrip_PointerReleased;
    }

    private void HookPathSpin()
    {
        if (_pathSpinVm is not null)
            _pathSpinVm.PropertyChanged -= OnPathSpinProperty;
        _pathSpinVm = (DataContext as MainWindowViewModel)?.Viewport;
        if (_pathSpinVm is null) return;
        _pathSpinVm.PropertyChanged += OnPathSpinProperty;
        SyncPathSpin();
    }

    private void OnPathSpinProperty(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewportViewModel.IsPathRecalculating))
            SyncPathSpin();
    }

    private void SyncPathSpin()
    {
        if (_pathSpinVm?.IsPathRecalculating == true)
            _pathSpin.Start();
        else
            _pathSpin.Stop();
    }

    private ToolbarViewModel? Toolbar =>
        DataContext is MainWindowViewModel root ? root.Toolbar : null;

    /// <summary>Click the build label to copy it (for pasting into a message to the team).</summary>
    private async void OnBuildLabelTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel root) return;
        await root.StatusBar.CopyBuildLabelAsync(TopLevel.GetTopLevel(this)?.Clipboard);
    }

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