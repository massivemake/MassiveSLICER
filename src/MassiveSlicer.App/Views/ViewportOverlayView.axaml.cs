using System;
using Avalonia.Controls;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class ViewportOverlayView : UserControl
{
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
        foreach (Control bar in new Control[] { SimTimelineBar, PlaybackTimelineBar, Lfam3WorkflowBar })
            bar.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty || e.Property == IsVisibleProperty)
                    UpdateBottomDockMargin();
            };
        DataContextChanged += (_, _) => UpdateBottomDockMargin();
    }

    private void UpdateBottomDockMargin()
    {
        if (DataContext is not ViewportViewModel vm) return;

        double lift = 8;
        foreach (Control bar in new Control[] { SimTimelineBar, PlaybackTimelineBar, Lfam3WorkflowBar })
            if (bar.IsVisible && bar.Bounds.Height > 0)
                lift = Math.Max(lift, bar.Margin.Bottom + bar.Bounds.Height + 16);

        vm.BottomDockMargin = new Avalonia.Thickness(8, 8, 8, lift);
    }

    private void OnGoToValidationIssue(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewportViewModel vm)
            vm.JumpToValidationIssue();
    }
}
