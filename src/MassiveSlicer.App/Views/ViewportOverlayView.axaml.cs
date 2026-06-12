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
    }
}
