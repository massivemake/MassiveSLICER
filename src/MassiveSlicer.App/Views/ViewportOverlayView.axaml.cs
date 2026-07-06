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
    }

    private void OnGoToValidationIssue(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewportViewModel vm)
            vm.JumpToValidationIssue();
    }
}