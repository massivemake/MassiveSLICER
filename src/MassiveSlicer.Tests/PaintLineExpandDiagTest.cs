using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.App.Views;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Regression for "Line selection mode only highlighted points": straight wall
/// panels slice each side into ONE long move, and 1-move spans used to render as a
/// midpoint dot. Lines mode must render a lone long move as the actual segment.
/// </summary>
public sealed class PaintLineHighlightPolicyTest
{
    [Fact]
    public void LongSingleMove_RendersAsLine()
    {
        // A 72mm wall side (box-panel scale) with a 6mm bead → line.
        var wall = new ToolpathMove(
            new Vector3(75, 147, 6), new Vector3(3, 147, 6), MoveKind.Extrude);
        Assert.True(ViewportView.SingleMoveRendersAsLine(wall, 6f));

        // Metre-long panel side → line.
        var panel = new ToolpathMove(
            new Vector3(0, 0, 3), new Vector3(2000, 0, 3), MoveKind.Extrude);
        Assert.True(ViewportView.SingleMoveRendersAsLine(panel, 6f));
    }

    [Fact]
    public void ShortBead_StillRendersAsPoint()
    {
        // A 3mm micro-bead (dense curved paths) stays a point highlight.
        var bead = new ToolpathMove(
            new Vector3(0, 0, 0), new Vector3(3, 0, 0), MoveKind.Extrude);
        Assert.False(ViewportView.SingleMoveRendersAsLine(bead, 6f));

        // Right at the threshold (1.5 × bead = 9mm) — not a line yet.
        var edge = new ToolpathMove(
            new Vector3(0, 0, 0), new Vector3(9, 0, 0), MoveKind.Extrude);
        Assert.False(ViewportView.SingleMoveRendersAsLine(edge, 6f));

        // Just past it — line.
        var past = new ToolpathMove(
            new Vector3(0, 0, 0), new Vector3(9.2f, 0, 0), MoveKind.Extrude);
        Assert.True(ViewportView.SingleMoveRendersAsLine(past, 6f));
    }
}
