using MassiveSlicer.App.Views;
using Xunit;

namespace MassiveSlicer.Tests;

public class ViewportPointerPolicyTest
{
    [Fact]
    public void Click_without_drag_selects_even_when_button_is_also_orbit()
    {
        Assert.True(ViewportPointerPolicy.IsClickSelectRelease(sawLeftPress: true, leftDragged: false));
        Assert.False(ViewportPointerPolicy.ConsumeOrbitPanRelease(isOrbitOrPanButton: true, leftDragged: false));
    }

    [Fact]
    public void Drag_on_orbit_button_does_not_select()
    {
        Assert.False(ViewportPointerPolicy.IsClickSelectRelease(sawLeftPress: true, leftDragged: true));
        Assert.True(ViewportPointerPolicy.ConsumeOrbitPanRelease(isOrbitOrPanButton: true, leftDragged: true));
    }

    [Fact]
    public void Release_without_matching_press_does_not_select()
    {
        Assert.False(ViewportPointerPolicy.IsClickSelectRelease(sawLeftPress: false, leftDragged: false));
    }

    [Fact]
    public void Non_orbit_click_still_selects()
    {
        Assert.True(ViewportPointerPolicy.IsClickSelectRelease(sawLeftPress: true, leftDragged: false));
        Assert.False(ViewportPointerPolicy.ConsumeOrbitPanRelease(isOrbitOrPanButton: false, leftDragged: false));
    }
}
