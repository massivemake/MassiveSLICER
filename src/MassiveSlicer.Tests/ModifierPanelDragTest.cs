using MassiveSlicer.ViewModels;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class ModifierPanelDragTest
{
    [Fact]
    public void Dropping_back_on_itself_is_a_no_op()
    {
        // 4 rows, dragging index 0, gap 0 (before itself) — should resolve back to 0.
        Assert.Equal(0, ModifierPanelViewModel.GapToIndex(gapIndex: 0, fromIndex: 0, count: 4));
    }

    [Fact]
    public void Dragging_down_past_one_row_lands_after_it()
    {
        // [A,B,C,D], drag A (0) down past B: expect result [B,A,C,D] -> A lands at index 1.
        Assert.Equal(1, ModifierPanelViewModel.GapToIndex(gapIndex: 2, fromIndex: 0, count: 4));
    }

    [Fact]
    public void Dragging_down_past_two_rows_lands_after_them()
    {
        // [A,B,C,D], drag A (0) down past B and C: expect [B,C,A,D] -> A lands at index 2.
        Assert.Equal(2, ModifierPanelViewModel.GapToIndex(gapIndex: 3, fromIndex: 0, count: 4));
    }

    [Fact]
    public void Dragging_up_past_one_row_lands_before_it()
    {
        // [A,B,C,D], drag C (2) up past B: expect [A,C,B,D] -> C lands at index 1.
        Assert.Equal(1, ModifierPanelViewModel.GapToIndex(gapIndex: 1, fromIndex: 2, count: 4));
    }

    [Fact]
    public void Dropping_at_the_very_end_lands_on_the_last_index()
    {
        // [A,B,C,D], drag A (0) to the end gap (4): expect [B,C,D,A] -> A lands at index 3.
        Assert.Equal(3, ModifierPanelViewModel.GapToIndex(gapIndex: 4, fromIndex: 0, count: 4));
    }

    [Fact]
    public void Gap_index_is_clamped_to_valid_range()
    {
        Assert.Equal(3, ModifierPanelViewModel.GapToIndex(gapIndex: 999, fromIndex: 0, count: 4));
        Assert.Equal(0, ModifierPanelViewModel.GapToIndex(gapIndex: -999, fromIndex: 3, count: 4));
    }
}
