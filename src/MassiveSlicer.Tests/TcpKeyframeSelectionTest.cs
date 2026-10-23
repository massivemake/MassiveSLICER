using MassiveSlicer.App;
using Xunit;

namespace MassiveSlicer.Tests;

public class TcpKeyframeSelectionTest
{
    [Fact]
    public void Another_scene_item_releases_the_key()
    {
        Assert.False(TcpKeyframeSelection.Keep(selectedIsActiveToolpath: false, selectedIsCurrentTool: false));
    }

    [Fact]
    public void A_click_on_the_selected_key_clears_it()
    {
        Assert.True(TcpKeyframeSelection.ClickClearsSelection(selectedIndex: 2, clickedIndex: 2));
        Assert.False(TcpKeyframeSelection.ClickClearsSelection(selectedIndex: 2, clickedIndex: 0));
        Assert.False(TcpKeyframeSelection.ClickClearsSelection(selectedIndex: -1, clickedIndex: 0));
    }

    [Fact]
    public void The_active_toolpath_keeps_the_key()
    {
        Assert.True(TcpKeyframeSelection.Keep(selectedIsActiveToolpath: true, selectedIsCurrentTool: false));
    }

    [Fact]
    public void The_current_tool_keeps_the_key()
    {
        Assert.True(TcpKeyframeSelection.Keep(selectedIsActiveToolpath: false, selectedIsCurrentTool: true));
    }

    [Fact]
    public void An_unselected_playhead_shows_the_blend()
    {
        Assert.True(TcpKeyframeSelection.ShowBlend(hasOrientKeys: true, keySelected: false));
        Assert.False(TcpKeyframeSelection.ShowBlend(hasOrientKeys: true, keySelected: true));
        Assert.False(TcpKeyframeSelection.ShowBlend(hasOrientKeys: false, keySelected: false));
    }

    [Fact]
    public void An_unselected_edit_inserts_a_key_on_an_empty_move()
    {
        Assert.True(TcpKeyframeSelection.InsertAtPlayhead(hasKeys: true, keySelected: false, playheadOccupied: false));
        Assert.False(TcpKeyframeSelection.InsertAtPlayhead(hasKeys: true, keySelected: false, playheadOccupied: true));
        Assert.False(TcpKeyframeSelection.InsertAtPlayhead(hasKeys: true, keySelected: true, playheadOccupied: false));
        Assert.False(TcpKeyframeSelection.InsertAtPlayhead(hasKeys: false, keySelected: false, playheadOccupied: false));
    }
}
