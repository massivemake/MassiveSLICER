using MassiveSlicer.ViewModels;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Covers the "handles get stuck together forever" bug reported against the presets-card range
/// filter: once Lower == Upper, a fixed proximity tie-break (e.g. "ties always go to Lower") can
/// only ever be escaped in one direction, because Lower can never numerically exceed Upper. These
/// tests exercise the actual reported scenario via the pure methods the view's pointer handlers
/// call, with no Avalonia UI involved.
/// </summary>
public sealed class NumericRangeFilterViewModelTest
{
    private static NumericRangeFilterViewModel MakeFilter(double min, double max)
    {
        var f = new NumericRangeFilterViewModel { FieldName = "Test", Selector = static _ => 0, DatasetMin = min, DatasetMax = max };
        f.LowerValue = min;
        f.UpperValue = max;
        return f;
    }

    [Fact]
    public void CoincidentHandles_DragRight_MovesUpperOnly()
    {
        var f = MakeFilter(4, 10);
        f.LowerValue = 6;
        f.UpperValue = 6;
        Assert.True(f.HandlesCoincident);

        var pressX = f.LowerThumbX;
        var isLower = f.DecideActiveLowerBound(pressX, pressX);
        Assert.Null(isLower); // undecided until the pointer actually moves

        isLower = f.DecideActiveLowerBound(pressX, pressX + 30);
        Assert.False(isLower); // moving right => Upper
        f.SetFromTrackX(isLower!.Value, pressX + 30);

        Assert.Equal(6, f.LowerValue);
        Assert.True(f.UpperValue > 6);
    }

    [Fact]
    public void CoincidentHandles_DragLeft_MovesLowerOnly()
    {
        var f = MakeFilter(4, 10);
        f.LowerValue = 6;
        f.UpperValue = 6;

        var pressX = f.LowerThumbX;
        var isLower = f.DecideActiveLowerBound(pressX, pressX - 30);
        Assert.True(isLower!.Value); // moving left => Lower
        f.SetFromTrackX(isLower.Value, pressX - 30);

        Assert.Equal(6, f.UpperValue);
        Assert.True(f.LowerValue < 6);
    }

    [Fact]
    public void CoincidentHandles_CanReopenRepeatedlyInAlternatingDirections()
    {
        // The exact failure mode reported: touch, try to separate, touch again, try again.
        var f = MakeFilter(0, 100);
        f.LowerValue = 50;
        f.UpperValue = 50;

        var x = f.LowerThumbX;
        var dir = f.DecideActiveLowerBound(x, x + 40)!.Value;
        f.SetFromTrackX(dir, x + 40);
        Assert.True(f.UpperValue > f.LowerValue);

        // Converge again (both handles dragged back to the same point) and confirm the OTHER
        // direction still escapes too.
        f.LowerValue = 50;
        f.UpperValue = 50;
        Assert.True(f.HandlesCoincident);

        x = f.LowerThumbX;
        dir = f.DecideActiveLowerBound(x, x - 40)!.Value;
        f.SetFromTrackX(dir, x - 40);
        Assert.True(f.LowerValue < f.UpperValue);
    }

    [Fact]
    public void SeparatedHandles_PickNearestByProximity_NotZOrder()
    {
        var f = MakeFilter(0, 100);
        f.LowerValue = 10;
        f.UpperValue = 90;
        Assert.False(f.HandlesCoincident);

        Assert.True(f.IsLowerNearer(f.LowerThumbX));
        Assert.False(f.IsLowerNearer(f.UpperThumbX));

        // Proximity — not "whichever was declared last in the view" — decides ownership.
        Assert.True(f.DecideActiveLowerBound(f.LowerThumbX, f.LowerThumbX)!.Value);
        Assert.False(f.DecideActiveLowerBound(f.UpperThumbX, f.UpperThumbX)!.Value);
    }

    [Fact]
    public void SetFromTrackX_NeverCrossesTheOtherBound()
    {
        var f = MakeFilter(0, 100);
        f.LowerValue = 40;
        f.UpperValue = 60;

        f.SetFromTrackX(isLowerBound: true, trackX: NumericRangeFilterViewModel.TrackWidthPx); // try to drag Low to the far right
        Assert.Equal(60, f.LowerValue); // clamped at Upper, not past it

        f.SetFromTrackX(isLowerBound: false, trackX: 0); // try to drag High to the far left
        Assert.Equal(60, f.UpperValue); // clamped at Lower (which is now 60), not below it
    }
}
