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
    private ViewportViewModel? _dockMarginVm;

    public ViewportOverlayView()
    {
        InitializeComponent();
        ScrubTrackGrid.SizeChanged += (_, e) =>
        {
            if (DataContext is ViewportViewModel vm)
                vm.ScrubTrackPixelWidth = e.NewSize.Width;
        };

        // Validation ticks (unreachable/singularity) are clickable: a press within a few
        // pixels of a marker snaps the scrubber + camera to that move. Tunnel routing so
        // a marker hit wins over the slider; anywhere else falls through to normal drag.
        ScrubTrackGrid.AddHandler(PointerPressedEvent, OnScrubTrackPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
        DataContextChanged += (_, _) =>
        {
            if (_dockMarginVm is not null)
                _dockMarginVm.PropertyChanged -= OnDockMarginVmPropertyChanged;
            _dockMarginVm = DataContext as ViewportViewModel;
            if (_dockMarginVm is not null)
                _dockMarginVm.PropertyChanged += OnDockMarginVmPropertyChanged;
            UpdateBottomDockMargin();
        };

        // Transform toolbar: swallow every press so none of it can reach the viewport underneath.
        // ViewportView subscribes PointerPressed on itself and calls Focus(), so a press falling
        // through this row takes focus back off the number field and then runs the viewport's own
        // click logic — which deselects when the ray misses the model. That is what made clicking a
        // number box feel like it "went through the window", and it depended on whether the hit
        // landed on the text or on the few pixels of padding around it.
        // Both press AND release: click-to-select runs on RELEASE, so swallowing only the press
        // still let the viewport pick — against the stale press position — and deselect the part,
        // which closed this very toolbar. That was the "clicking a number box deselects the mesh
        // about half the time" report, and it happened dead-centre of a field, not just at the edges.
        foreach (Control row in new Control[] { MoveValuesRow, RotateValuesRow })
        {
            row.AddHandler(PointerPressedEvent,  OnTransformRowPointerPressed, handledEventsToo: true);
            row.AddHandler(PointerReleasedEvent, OnTransformRowPointerPressed, handledEventsToo: true);
        }

        // Clicking an axis letter steps a clean additive 90°; Alt reverses.
        foreach (Control label in new Control[] { StepAxisX, StepAxisY, StepAxisZ })
            label.AddHandler(PointerPressedEvent, OnStepAxisPointerPressed, handledEventsToo: true);

        // Long-press region-select icon → toggle Square ↔ Lasso.
        RegionSelectButton.AddHandler(PointerPressedEvent, OnRegionSelectPointerPressed, handledEventsToo: true);
        RegionSelectButton.AddHandler(PointerReleasedEvent, OnRegionSelectPointerReleased, handledEventsToo: true);
        RegionSelectButton.AddHandler(PointerCaptureLostEvent, OnRegionSelectCaptureLost, handledEventsToo: true);
    }

    /// <summary>
    /// Marks any press inside a transform value row handled, so it never reaches the viewport's own
    /// pointer handler. Deliberately blanket rather than per-control: the row's padding, the axis
    /// letters and the gaps between fields are all dead space that would otherwise fall through.
    /// </summary>
    private static void OnTransformRowPointerPressed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => e.Handled = true;

    /// <summary>
    /// Clicking an axis letter rotates a clean additive 90° about the part's own axis; holding Alt
    /// goes the other way. Additive so four clicks land back exactly where they started.
    /// </summary>
    private void OnStepAxisPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { Tag: string tag } || !int.TryParse(tag, out int axis)) return;
        if (DataContext is not ViewportViewModel vm) return;

        // The result string is for the console command; here the refreshed number boxes are the
        // feedback, so it is deliberately dropped.
        _ = vm.StepRotation(axis, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
    }

    /// <summary>Snap-to-error: Alt-click or double-click within ±6 px of a validation
    /// tick (red unreachable / purple singularity / orange collision) jumps the
    /// scrubber + camera to that move. Plain click/drag always scrubs normally —
    /// the snap must never steal the slider on tick-dense toolpaths.</summary>
    private void OnScrubTrackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ViewportViewModel vm) return;
        bool wantsSnap = e.ClickCount >= 2
                         || e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (!wantsSnap) return;
        double x = e.GetPosition(ScrubTrackGrid).X;

        double? best = null;
        double bestDist = 6.0; // px hit tolerance
        foreach (var list in new[] { vm.ScrubUnreachableMarkers, vm.ScrubSingularityMarkers, vm.ScrubCollisionMarkers })
        {
            if (list is null) continue;
            foreach (var mx in list)
            {
                double d = Math.Abs(mx - x);
                if (d < bestDist) { bestDist = d; best = mx; }
            }
        }
        if (best is null) return;

        vm.JumpToScrubPixel(best.Value);
        e.Handled = true;
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
        // Long-press already toggled mode. ToggleButton still raises Click on release and
        // would flip IsChecked off — suppress that and re-arm after input processing.
        if (_regionSelectLongPressFired)
        {
            e.Handled = true;
            _regionSelectLongPressFired = false;
            // After ToggleButton Click (which may flip IsChecked) — re-arm tool.
            Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is ViewportViewModel vm)
                    vm.PaintBoxSelectActive = true;
            }, DispatcherPriority.Loaded);
            return;
        }
        _regionSelectLongPressFired = false;
    }

    private void OnRegionSelectCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _regionSelectLongPress?.Stop();
        _regionSelectLongPress = null;
        _regionSelectLongPressFired = false;
    }

    private void OnDockMarginVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Lfam3WorkflowMaxHeight drives the lift below — it changes the instant a phase
        // card or the embedded Live I/O expands, ahead of any layout pass touching Bounds.
        if (e.PropertyName == nameof(ViewportViewModel.Lfam3WorkflowMaxHeight))
            UpdateBottomDockMargin();
    }

    private void UpdateBottomDockMargin()
    {
        if (DataContext is not ViewportViewModel vm) return;

        double lift = 8;
        foreach (Control bar in new Control[] { SimTimelineBar, PlaybackTimelineBar })
            if (bar.IsVisible && bar.Bounds.Height > 0)
                lift = Math.Max(lift, bar.Margin.Bottom + bar.Bounds.Height + 16);

        // Lfam3WorkflowBar's phase-detail cards and embedded Live I/O monitor float above
        // the collapsed panel via negative-margin + ClipToBounds=False (a deliberate trick
        // so expanding a card doesn't push the header row down) — so Bounds.Height only
        // ever reports the COLLAPSED height and under-reports the true visual footprint
        // whenever a card or Live I/O is open. That under-count is why the ERP / Live I/O
        // corner docks used to sit low enough to overlap the timeline on LFAM 3. Use the
        // analytically-computed max height (already modelled correctly) as a floor.
        if (Lfam3WorkflowBar.IsVisible)
        {
            double workflowHeight = Math.Max(Lfam3WorkflowBar.Bounds.Height, vm.Lfam3WorkflowMaxHeight);
            lift = Math.Max(lift, Lfam3WorkflowBar.Margin.Bottom + workflowHeight + 16);
        }

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

    private void OnSimTimelineSliderDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is ViewportViewModel vm)
            vm.AddSimCameraKeyframeCommand.Execute(null);
    }
}
