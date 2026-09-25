using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// ROBOT CELL TOOL # / BASE # when the cell changes: a number the new cell does not have must
/// never survive (it went blank on screen while the stale number kept exporting), and a new
/// robot starts on its own default tool.
/// </summary>
public sealed class RobotToolOnCellSwitchTest
{
    private static ToolCellConfig Tool(int krl, string name, bool isDefault = false)
        => new() { KrlIndex = krl, Name = name, ModelPath = "", Default = isDefault };

    private static readonly ToolCellConfig[] Lfam1 = [Tool(1, "HF Extruder", isDefault: true)];
    private static readonly ToolCellConfig[] Lfam2 = [Tool(1, "HF Extruder"), Tool(2, "HV Extruder", isDefault: true)];
    private static readonly KrlBaseEntry[] OneBase = [new() { Name = "massiveb1", Index = 1 }];
    private static readonly KrlBaseEntry[] Lfam3Bases =
        [new() { Name = "Rotary Table (Wood)", Index = 1 }, new() { Name = "Rotary Table", Index = 2 }, new() { Name = "HEATED-BED", Index = 6 }];

    private static RobotPanelViewModel Robot(ToolCellConfig[] tools, KrlBaseEntry[] bases, int tool, int baseNo)
    {
        var robot = new RobotPanelViewModel();
        robot.SetToolLibrary(tools);
        robot.SetKrlFrameOptions(tools, bases, tool, baseNo);
        return robot;
    }

    [Fact]
    public void A_tool_number_the_cell_lacks_falls_back_to_its_default_instead_of_going_blank()
    {
        // LFAM 2's HV tool 2 carried onto LFAM 1, which only has tool 1.
        var robot = Robot(Lfam1, OneBase, tool: 2, baseNo: 1);
        Assert.Equal(1, robot.KrlToolIndex);
        Assert.Equal(0, robot.KrlToolSelectedIndex);   // "1: HF Extruder", not blank
    }

    [Fact]
    public void A_new_robot_with_no_carried_tool_starts_on_its_default()
    {
        // Switching cells passes 0 — LFAM 2 must come up on its default HV extruder (tool 2).
        var robot = Robot(Lfam2, OneBase, tool: 0, baseNo: 0);
        Assert.Equal(2, robot.KrlToolIndex);
        Assert.Equal("2: HV Extruder", robot.KrlToolOptions[robot.KrlToolSelectedIndex]);
    }

    [Fact]
    public void A_tool_the_cell_has_is_kept_for_restores_and_reloads()
    {
        var robot = Robot(Lfam2, OneBase, tool: 1, baseNo: 1);
        Assert.Equal(1, robot.KrlToolIndex);
    }

    [Fact]
    public void A_single_base_is_filled_in_but_a_multi_base_cell_is_never_guessed()
    {
        Assert.Equal(1, Robot(Lfam2, OneBase, tool: 0, baseNo: 0).KrlBaseIndex);
        Assert.Equal(1, Robot(Lfam1, OneBase, tool: 0, baseNo: 6).KrlBaseIndex);   // 6 is not LFAM 1's

        var lfam3 = Robot(Lfam2, Lfam3Bases, tool: 0, baseNo: 0);
        Assert.Equal(-1, lfam3.KrlBaseSelectedIndex);                              // left for the operator
        Assert.Equal(6, Robot(Lfam2, Lfam3Bases, tool: 0, baseNo: 6).KrlBaseIndex);
    }
}
