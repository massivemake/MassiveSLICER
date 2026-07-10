using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class ViewportOverlayView : UserControl
{
    private DispatcherTimer? _regionSelectLongPress;
    private bool _regionSelectLongPressFired;

    public ViewportOverlayView()
    {
        InitializeComponent();
        ScrubTrackGrid.SizeChanged += (_, e) =>
        {
            if (DataContext is ViewportViewModel vm)
                vm.ScrubTrackPixelWidth = e.NewSize.Width;
        };
        KeyframeLane.KeyframeClicked = i =>
            (DataContext as ViewportViewModel)?.OnKeyframeLaneClicked?.Invoke(i);
        KeyframeLane.InfluenceDragged = (i, left, x, commit) =>
            (DataContext as ViewportViewModel)?.OnKeyframeInfluenceDragged?.Invoke(i, left, x, commit);

        // Keep the bottom corner docks (ERP / Live I/O) 16px above whichever
        // timeline bar is showing; heights vary (keyframe lane, workflow bar).
        // Bottom-right legends stack above the Live I/O dock in turn.
        foreach (Control bar in new Control[] { SimTimelineBar, PlaybackTimelineBar, Lfam3WorkflowBar, LiveIoDock })
            bar.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty || e.Property == IsVisibleProperty)
                    UpdateBottomDockMargin();
            };
        DataContextChanged += (_, _) => UpdateBottomDockMargin();

        // Long-press region-select icon → toggle Square ↔ Lasso.
        RegionSelectButton.AddHandler(PointerPressedEvent, OnRegionSelectPointerPressed, handledEventsToo: true);
        RegionSelectButton.AddHandler(PointerReleasedEvent, OnRegionSelectPointerReleased, handledEventsToo: true);
        RegionSelectButton.AddHandler(PointerCaptureLostEvent, OnRegionSelectCaptureLost, handledEventsToo: true);
    }

    private void OnRegionSelectPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(RegionSelectButton).Properties.IsLeftButtonPressed) return;
        _regionSelectLongPressFired = false;
        _regionSelectLongPress?.Stop();
        _regionSelectLongPress = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _regionSelectLongPress.Tick += (_, _) =>
        {
            _regionSelectLongPress?.Stop();
            _regionSelectLongPressFired = true;
            if (DataContext is ViewportViewModel vm)
                vm.TogglePaintRegionSelectMode();
        };
        _regionSelectLongPress.Start();
    }

    private void OnRegionSelectPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _regionSelectLongPress?.Stop();
        _regionSelectLongPress = null;
        // Long-press already toggled mode — keep the tool armed (toggle button may have
        // flipped off then on; force active so a long-press never leaves the tool off).
        if (_regionSelectLongPressFired && DataContext is ViewportViewModel vm)
            vm.PaintBoxSelectActive = true;
        _regionSelectLongPressFired = false;
    }

    private void OnRegionSelectCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _regionSelectLongPress?.Stop();
        _regionSelectLongPress = null;
        _regionSelectLongPressFired = false;
    }

    private void UpdateBottomDockMargin()
    {
        if (DataContext is not ViewportViewModel vm) return;

        double lift = 8;
        foreach (Control bar in new Control[] { SimTimelineBar, PlaybackTimelineBar, Lfam3WorkflowBar })
            if (bar.IsVisible && bar.Bounds.Height > 0)
                lift = Math.Max(lift, bar.Margin.Bottom + bar.Bounds.Height + 16);

        vm.BottomDockMargin = new Avalonia.Thickness(8, 8, 8, lift);

        // Legends sit above the Live I/O dock (whose margin is the lift just computed).
        double legendLift = lift;
        if (LiveIoDock.IsVisible && LiveIoDock.Bounds.Height > 0)
            legendLift = lift + LiveIoDock.Bounds.Height + 8;
        vm.BottomRightLegendMargin = new Avalonia.Thickness(8, 8, 8, legendLift);
    }

    private void OnGoToValidationIssue(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewportViewModel vm)
            vm.JumpToValidationIssue();
    }
}
