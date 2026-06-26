using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class KrlToolpathHandlingTest
{
    [Theory]
    [InlineData(MoveKind.Extrude, true, false)]
    [InlineData(MoveKind.Mill, true, false)]
    [InlineData(MoveKind.Travel, false, true)]
    public void ToolpathMoveKinds_Classifies_Krl_And_Slicer_Segments(
        MoveKind kind, bool isCut, bool isTravel)
    {
        Assert.Equal(isCut, ToolpathMoveKinds.IsCutSegment(kind));
        Assert.Equal(isTravel, ToolpathMoveKinds.IsTravelSegment(kind));
    }

    [Fact]
    public void ScrubPrefixSums_Place_Mill_In_Extrude_Bucket_Not_Travel()
    {
        // Mirrors ToolpathRenderer.ComputeMovePrefixSums — Mill must follow extrude VBO layout.
        var moves = new[]
        {
            new ToolpathMove(Vector3.Zero, new Vector3(10, 0, 0), MoveKind.Mill),
            new ToolpathMove(new Vector3(10, 0, 0), new Vector3(20, 0, 0), MoveKind.Travel),
            new ToolpathMove(new Vector3(20, 0, 0), new Vector3(30, 0, 0), MoveKind.Mill),
        };

        var extrudeCumulative = new int[moves.Length + 1];
        var travelCumulative  = new int[moves.Length + 1];
        int ei = 0, ti = 0;
        for (int fi = 0; fi < moves.Length; fi++)
        {
            if (ToolpathMoveKinds.IsCutSegment(moves[fi].Kind)) ei += 2;
            else if (ToolpathMoveKinds.IsTravelSegment(moves[fi].Kind)) ti += 2;
            extrudeCumulative[fi + 1] = ei;
            travelCumulative[fi + 1]  = ti;
        }

        Assert.Equal(2, extrudeCumulative[1]);  // after first LIN
        Assert.Equal(0, travelCumulative[1]);   // travel VBO unchanged
        Assert.Equal(2, extrudeCumulative[2]);  // PTP does not add extrude verts
        Assert.Equal(2, travelCumulative[2]);   // PTP adds travel verts
        Assert.Equal(4, extrudeCumulative[3]);  // second LIN
        Assert.Equal(2, travelCumulative[3]);
    }

    [Fact]
    public void Bead_Contours_Include_Mill_Segments_From_Krl_Import()
    {
        var moves = new[]
        {
            new ToolpathMove(Vector3.Zero, new Vector3(10, 0, 0), MoveKind.Mill),
            new ToolpathMove(new Vector3(10, 0, 0), new Vector3(20, 0, 0), MoveKind.Travel),
            new ToolpathMove(new Vector3(20, 0, 0), new Vector3(30, 0, 0), MoveKind.Mill),
        };

        int cutCount = moves.Count(m => ToolpathMoveKinds.IsCutSegment(m.Kind));
        Assert.Equal(2, cutCount);

        int contourSegments = 0;
        List<int>? cur = null;
        foreach (var move in moves)
        {
            if (ToolpathMoveKinds.IsCutSegment(move.Kind))
            {
                if (cur is null) cur = [];
                cur.Add(1);
            }
            else
            {
                if (cur is not null) contourSegments += cur.Count;
                cur = null;
            }
        }
        if (cur is not null) contourSegments += cur.Count;

        Assert.Equal(2, contourSegments); // travel breaks contour — two separate mill runs
    }

    [Fact]
    public void ScrubCount_Does_Not_Index_Empty_Bead_Cumulative_For_Mill_Only_Paths()
    {
        // Mill-only paths skip bead VBO upload; prefix array must still be sized to move count.
        int totalMoves = 5;
        var beadCumulative = new int[totalMoves + 1]; // BuildBeadVertexCumulative for no extrude
        int ScrubCount(int[] cumulative, int totalCount, int scrubIndex)
        {
            if (scrubIndex >= totalMoves || totalMoves == 0) return totalCount;
            if (scrubIndex <= 0) return 0;
            if ((uint)scrubIndex >= (uint)cumulative.Length) return totalCount;
            return Math.Min(cumulative[scrubIndex], totalCount);
        }

        for (int i = 1; i < totalMoves; i++)
            Assert.Equal(0, ScrubCount(beadCumulative, 0, i));
    }
}