using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public sealed class ToolpathSeamEditorTest
{
    // Builds a single closed square loop: travel -> (0,0), then extrudes around the square.
    private static (Toolpath tp, ToolpathLayer layer) BuildSquareLoop()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitZ };

        var v0 = new Vector3(0, 0, 0);
        var v1 = new Vector3(10, 0, 0);
        var v2 = new Vector3(10, 10, 0);
        var v3 = new Vector3(0, 10, 0);

        layer.Moves.Add(new ToolpathMove(new Vector3(-5, -5, 0), v0, MoveKind.Travel)); // entry travel
        layer.Moves.Add(new ToolpathMove(v0, v1, MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(v1, v2, MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(v2, v3, MoveKind.Extrude));
        layer.Moves.Add(new ToolpathMove(v3, v0, MoveKind.Extrude)); // closing
        layer.Contours.Add(new ContourSpan(Start: 1, Count: 4, Closed: true, EntryTravelIndex: 0));

        tp.Layers.Add(layer);
        return (tp, layer);
    }

    [Fact]
    public void ApplySeams_rotates_loop_to_vertex_nearest_seam()
    {
        var (tp, layer) = BuildSquareLoop();

        int moved = ToolpathSeamEditor.ApplySeams(tp, [new Vector2(11, 11)]); // nearest to corner (10,10)

        Assert.Equal(1, moved);
        // Loop now starts at (10,10) and remains a closed 4-move cycle.
        Assert.Equal(new Vector3(10, 10, 0), layer.Moves[1].From);
        Assert.Equal(new Vector3(10, 10, 0), layer.Moves[4].To);   // still closes back to the start
        Assert.Equal(new Vector3(10, 10, 0), layer.Moves[0].To);   // entry travel retargeted
        // All four vertices are still present (a rotation, not a rewrite).
        var starts = new[] { layer.Moves[1].From, layer.Moves[2].From, layer.Moves[3].From, layer.Moves[4].From };
        Assert.Contains(new Vector3(0, 0, 0), starts);
        Assert.Contains(new Vector3(10, 0, 0), starts);
        Assert.Contains(new Vector3(0, 10, 0), starts);
    }

    [Fact]
    public void ApplySeams_is_deterministic_when_reapplied()
    {
        var (tp, layer) = BuildSquareLoop();
        ToolpathSeamEditor.ApplySeams(tp, [new Vector2(11, 11)]);
        int movedAgain = ToolpathSeamEditor.ApplySeams(tp, [new Vector2(11, 11)]); // same target
        Assert.Equal(0, movedAgain); // already seamed there — no change
        Assert.Equal(new Vector3(10, 10, 0), layer.Moves[1].From);
    }

    [Fact]
    public void ApplySeams_assigns_each_loop_to_its_nearest_point()
    {
        var (tp, layer) = BuildSquareLoop();
        // Two seam points; the loop should pick the nearer one (near origin corner).
        int moved = ToolpathSeamEditor.ApplySeams(tp, [new Vector2(0.2f, 0.2f), new Vector2(100, 100)]);
        Assert.Equal(0, moved); // loop already starts at (0,0), nearest to (0.2,0.2)
        Assert.Equal(new Vector3(0, 0, 0), layer.Moves[1].From);
    }
}
