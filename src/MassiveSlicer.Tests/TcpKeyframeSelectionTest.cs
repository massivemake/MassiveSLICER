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
    public void An_unselected_playhead_keeps_the_sliders_on_the_baseline()
    {
        Assert.True(TcpKeyframeSelection.ShowBlend(hasOrientKeys: true, keySelected: false));
        Assert.False(TcpKeyframeSelection.ShowBlend(hasOrientKeys: true, keySelected: true));
        Assert.False(TcpKeyframeSelection.ShowBlend(hasOrientKeys: false, keySelected: false));
    }

    [Fact]
    public void Ctrl_moves_the_key_and_shift_sizes_the_tail()
    {
        Assert.Equal(KeyframeLaneGesture.MoveKey,
            TcpKeyframeSelection.Gesture(ctrl: true, shift: false, onDiamond: true, onTail: false, ctrlTarget: true));
        Assert.Equal(KeyframeLaneGesture.MoveKey,
            TcpKeyframeSelection.Gesture(ctrl: true, shift: true, onDiamond: false, onTail: true, ctrlTarget: true));
        Assert.Equal(KeyframeLaneGesture.ResizeTail,
            TcpKeyframeSelection.Gesture(ctrl: false, shift: true, onDiamond: false, onTail: true, ctrlTarget: false));
        Assert.Equal(KeyframeLaneGesture.SelectDiamond,
            TcpKeyframeSelection.Gesture(ctrl: false, shift: false, onDiamond: true, onTail: false, ctrlTarget: false));
        Assert.Equal(KeyframeLaneGesture.SelectTail,
            TcpKeyframeSelection.Gesture(ctrl: false, shift: false, onDiamond: false, onTail: true, ctrlTarget: false));
        Assert.Equal(KeyframeLaneGesture.SelectDiamond,
            TcpKeyframeSelection.Gesture(ctrl: false, shift: true, onDiamond: true, onTail: false, ctrlTarget: false));
        Assert.Equal(KeyframeLaneGesture.None,
            TcpKeyframeSelection.Gesture(ctrl: true, shift: false, onDiamond: false, onTail: false, ctrlTarget: false));
    }

    [Fact]
    public void An_angle_key_does_not_move_the_cuts()
    {
        Assert.False(TcpKeyframeSelection.StoresPositionOffset(translatedTcp: false));
        Assert.True(TcpKeyframeSelection.StoresPositionOffset(translatedTcp: true));
    }

    [Fact]
    public void An_unselected_edit_does_not_insert_a_key()
    {
        Assert.False(TcpKeyframeSelection.InsertAtPlayhead(hasKeys: true, keySelected: false, playheadOccupied: false));
        Assert.False(TcpKeyframeSelection.InsertAtPlayhead(hasKeys: true, keySelected: false, playheadOccupied: true));
        Assert.False(TcpKeyframeSelection.InsertAtPlayhead(hasKeys: true, keySelected: true, playheadOccupied: false));
        Assert.False(TcpKeyframeSelection.InsertAtPlayhead(hasKeys: false, keySelected: false, playheadOccupied: false));
    }
}
