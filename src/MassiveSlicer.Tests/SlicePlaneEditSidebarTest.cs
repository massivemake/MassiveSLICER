using MassiveSlicer.App;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

public sealed class SlicePlaneEditSidebarTest
{
    [Fact]
    public void Pick_window_includes_the_ghost_layers_under_the_active_line()
    {
        int[] ends = [10, 25, 40, 60];
        var (start, limit) = SlicePlanePick.Window(currentLayer: 3, ends, belowCount: 3);
        Assert.Equal(0, start);
        Assert.Equal(60, limit);

        (start, limit) = SlicePlanePick.Window(currentLayer: 2, ends, belowCount: 1);
        Assert.Equal(10, start);
        Assert.Equal(40, limit);
    }

    [Fact]
    public void Entering_and_leaving_edit_does_not_collapse_the_workflow_cards()
    {
        var panel = new RightPanelViewModel
        {
            StepModelExpanded = true,
            StepSliceExpanded = true,
            StepPatternExpanded = false,
            StepToolpathExpanded = true,
        };

        panel.ApplyPaintEditMode(true);
        Assert.True(panel.IsPaintEditOpen);
        Assert.False(panel.ShowWorkflowCards);
        Assert.True(panel.StepModelExpanded);
        Assert.True(panel.StepSliceExpanded);
        Assert.True(panel.StepToolpathExpanded);

        panel.ApplyPaintEditMode(false);
        Assert.True(panel.ShowWorkflowCards);
        Assert.True(panel.StepModelExpanded);
        Assert.True(panel.StepSliceExpanded);
    }

    [Fact]
    public void Leaving_edit_reopens_cards_a_previous_session_collapsed()
    {
        var panel = new RightPanelViewModel
        {
            StepModelExpanded = false,
            StepSliceExpanded = false,
            StepPatternExpanded = false,
            StepToolpathExpanded = false,
        };

        panel.ApplyPaintEditMode(true);
        panel.ApplyPaintEditMode(false);

        Assert.True(panel.StepModelExpanded);
        Assert.True(panel.StepSliceExpanded);
        Assert.True(panel.StepPatternExpanded);
        Assert.True(panel.StepToolpathExpanded);
        Assert.True(panel.ShowWorkflowCards);
    }
}
