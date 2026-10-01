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

    [Fact]
    public void Slice_edit_click_keeps_the_hovered_span()
    {
        Assert.True(ViewportPointerPolicy.SliceEditClickSelects(
            sliceEdit: true, hand: false, regionSelect: false, lineSelectArmed: true));
        Assert.False(ViewportPointerPolicy.SliceEditClickSelects(
            sliceEdit: true, hand: true, regionSelect: false, lineSelectArmed: true));
        Assert.False(ViewportPointerPolicy.SliceEditClickSelects(
            sliceEdit: true, hand: false, regionSelect: true, lineSelectArmed: true));
    }

    [Fact]
    public void Mill_area_does_not_steal_2d_edit_clicks()
    {
        Assert.False(ViewportPointerPolicy.MillPaintCapturesPointer(
            areaToolArmed: true, millStepActive: false, sliceEditOpen: true));
        Assert.False(ViewportPointerPolicy.MillPaintCapturesPointer(
            areaToolArmed: true, millStepActive: true, sliceEditOpen: true));
        Assert.True(ViewportPointerPolicy.MillPaintCapturesPointer(
            areaToolArmed: true, millStepActive: true, sliceEditOpen: false));
    }
}
