using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class KrlToolChangeSequenceParserTest
{
    [Fact]
    public void Extruder_Pick_parses_with_resolvable_waypoints()
    {
        if (!KrlToolChangeSequenceParser.IsSequenceAvailable("Extruder_Pick"))
            return;

        var seq = KrlToolChangeSequenceParser.Parse("Extruder_Pick");
        Assert.Equal("extruder", seq.Definition.ToolKey);
        Assert.Equal("HV Extruder", seq.Definition.CellToolName);
        Assert.True(seq.Waypoints.Count >= 4);
        Assert.Contains(seq.Waypoints, w => w.Kind == "cart");
    }

    [Fact]
    public void Path_builder_produces_dense_polyline()
    {
        if (!KrlToolChangeSequenceParser.IsSequenceAvailable("Scanner_Pick"))
            return;

        var seq  = KrlToolChangeSequenceParser.Parse("Scanner_Pick");
        var path = ToolChangeSequencePathBuilder.Build(seq);
        Assert.NotNull(path);
        Assert.True(path!.DensePoints.Count >= 8);
        Assert.True(path.TotalLength > 100f);
    }

    [Fact]
    public void Scanner_Pick_detects_tool_attach_event()
    {
        if (!KrlToolChangeSequenceParser.IsSequenceAvailable("Scanner_Pick"))
            return;

        var seq  = KrlToolChangeSequenceParser.Parse("Scanner_Pick");
        var path = ToolChangeSequencePathBuilder.Build(seq);
        Assert.NotNull(path?.ToolEvent);
        Assert.True(path!.ToolEvent!.Attach);
        Assert.Equal("Scanner", path.ToolEvent.CellToolName);
    }

    [Fact]
    public void Scanner_Deposit_resolves_cartesian_waypoints_for_overlay()
    {
        if (!KrlToolChangeSequenceParser.IsSequenceAvailable("Scanner_Deposit"))
            return;

        var seq  = KrlToolChangeSequenceParser.Parse("Scanner_Deposit");
        var path = ToolChangeSequencePathBuilder.Build(seq);
        Assert.NotNull(path);
        Assert.True(path!.ResolvedWaypoints.Count >= 2);
        Assert.Contains(path.ResolvedWaypoints, w => w.Waypoint.Name.StartsWith("gX", StringComparison.Ordinal));
        Assert.True(path.TotalLength > 100f);
    }
}