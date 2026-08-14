using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

/// <summary>
/// Horizontal-visibility scope: sweep rays across a layer, pattern what they hit, leave what is
/// shadowed straight.
///
/// <para>
/// The case that motivated it: a part whose contours all slice OPEN. Nesting depth is a
/// point-in-polygon test, so an open chain is never "inside" anything and the whole part comes
/// back at depth 0 — every wall reading as outermost, nothing to exclude. Several tests here
/// deliberately use open or fragmented contours for that reason; a version of this that only
/// ever saw closed loops would pass while the real part failed, which is precisely how the
/// earlier depth-based scope shipped looking correct.
/// </para>
/// </summary>
public class SkinRaycastVisibilityTest
{
    private const float Bead = 8f;

    /// <summary>Ring of extrude moves. <paramref name="closed"/> false leaves a gap in the loop.</summary>
    private static List<ToolpathMove> Ring(float cx, float cy, float r, int seg, bool closed = true)
    {
        var moves = new List<ToolpathMove>();
        int last = closed ? seg : (int)(seg * 0.75f);
        for (int i = 0; i < last; i++)
        {
            float a0 = i / (float)seg * MathF.Tau, a1 = (i + 1) / (float)seg * MathF.Tau;
            moves.Add(new ToolpathMove(
                new Vector3(cx + r * MathF.Cos(a0), cy + r * MathF.Sin(a0), 0f),
                new Vector3(cx + r * MathF.Cos(a1), cy + r * MathF.Sin(a1), 0f),
                MoveKind.Extrude));
        }
        return moves;
    }

    private static bool[] Mask(List<ToolpathMove> moves)
        => SkinRaycastVisibility.BuildInteriorMask(moves, Bead, Bead);

    /// <summary>Fraction of a move range flagged interior.</summary>
    private static float InteriorFrac(bool[] mask, int start, int count)
    {
        int n = 0;
        for (int i = start; i < start + count; i++) if (mask[i]) n++;
        return n / (float)count;
    }

    [Fact]
    public void OuterRingIsVisibleAndInnerRingIsNot()
    {
        var outer = Ring(0f, 0f, 200f, 240);
        var inner = Ring(0f, 0f, 80f, 120);
        var moves = new List<ToolpathMove>(outer);
        moves.AddRange(inner);

        var mask = Mask(moves);

        Assert.True(InteriorFrac(mask, 0, outer.Count) < 0.02f,
            "the outer ring should be reachable by horizontal rays");
        Assert.True(InteriorFrac(mask, outer.Count, inner.Count) > 0.98f,
            "the inner ring is shadowed by the outer one and should read as interior");
    }

    /// <summary>Arc of extrude moves from <paramml name="a0"/> to <paramref name="a1"/> radians.</summary>
    private static List<ToolpathMove> Arc(float r, float a0, float a1, int seg)
    {
        var moves = new List<ToolpathMove>();
        for (int i = 0; i < seg; i++)
        {
            float t0 = a0 + (a1 - a0) * i / seg, t1 = a0 + (a1 - a0) * (i + 1) / seg;
            moves.Add(new ToolpathMove(
                new Vector3(r * MathF.Cos(t0), r * MathF.Sin(t0), 0f),
                new Vector3(r * MathF.Cos(t1), r * MathF.Sin(t1), 0f),
                MoveKind.Extrude));
        }
        return moves;
    }

    /// <summary>
    /// The discriminator against nesting depth, built the way the real part slices: the outer
    /// boundary arrives as SEVERAL separate open arcs rather than one closed loop.
    ///
    /// <para>
    /// No individual arc is a polygon, so point-in-polygon has nothing to test containment
    /// against and every contour lands at depth 0 — which is exactly the measured failure
    /// (6,676,002 walls, all "outer", zero interior). The arcs still block rays collectively,
    /// so visibility separates them cleanly where nesting cannot.
    /// </para>
    /// </summary>
    [Fact]
    public void SeparatesFragmentedOpenContoursThatNestingDepthCannot()
    {
        const float Gap = 0.02f;               // ~4mm of gap at r=200: a seam, not a window
        var moves = new List<ToolpathMove>();
        int outerCount = 0;
        for (int q = 0; q < 4; q++)
        {
            var arc = Arc(200f, q * MathF.Tau / 4f + Gap, (q + 1) * MathF.Tau / 4f - Gap, 60);
            moves.AddRange(arc);
            outerCount += arc.Count;
        }
        var inner = Ring(0f, 0f, 80f, 120);
        moves.AddRange(inner);

        var mask = Mask(moves);

        Assert.True(InteriorFrac(mask, 0, outerCount) < 0.05f,
            "the fragmented outer boundary should still be visible skin");
        Assert.True(InteriorFrac(mask, outerCount, inner.Count) > 0.9f,
            $"the shadowed inner ring should read as interior, but only " +
            $"{InteriorFrac(mask, outerCount, inner.Count) * 100:F0}% did");
    }

    /// <summary>
    /// A genuine opening is NOT a failure: rays that reach through a real gap are supposed to
    /// light what they hit. This pins that down so the isolated-hit cleanup can never grow into
    /// something that suppresses real visibility.
    /// </summary>
    [Fact]
    public void RaysThroughALargeOpeningDoLightWhatTheyReach()
    {
        var outer = Ring(0f, 0f, 200f, 240, closed: false);   // a quarter of the ring is missing
        var inner = Ring(0f, 0f, 80f, 120);
        var moves = new List<ToolpathMove>(outer);
        moves.AddRange(inner);

        var mask = Mask(moves);
        float lit = 1f - InteriorFrac(mask, outer.Count, inner.Count);

        Assert.True(lit > 0.05f,
            "a quarter-open shell should let rays reach some of the inner ring");
        Assert.True(lit < 0.6f,
            $"only the part facing the opening should light up, but {lit * 100:F0}% did");
    }

    [Fact]
    public void ConcaveMouthIsStillVisible()
    {
        // A deep notch cut into one side. Its floor is genuinely visible from outside even though
        // it sits well inside the part's overall radius, which is what a centre-radial test gets
        // wrong and a real sweep gets right.
        var moves = new List<ToolpathMove>();
        void Run(Vector3 a, Vector3 b)
        {
            const int N = 40;
            for (int i = 0; i < N; i++)
                moves.Add(new ToolpathMove(Vector3.Lerp(a, b, i / (float)N),
                                           Vector3.Lerp(a, b, (i + 1) / (float)N), MoveKind.Extrude));
        }
        Vector3 P(float x, float y) => new(x, y, 0f);

        Run(P(-200f, -200f), P(200f, -200f));   // bottom
        Run(P(200f, -200f), P(200f, 200f));     // right
        Run(P(200f, 200f), P(60f, 200f));       // top right
        Run(P(60f, 200f), P(60f, -40f));        // notch wall down
        int floorStart = moves.Count;
        Run(P(60f, -40f), P(-60f, -40f));       // notch FLOOR — deep inside, still visible
        int floorCount = moves.Count - floorStart;
        Run(P(-60f, -40f), P(-60f, 200f));      // notch wall up
        Run(P(-60f, 200f), P(-200f, 200f));     // top left
        Run(P(-200f, 200f), P(-200f, -200f));   // left

        var mask = Mask(moves);

        Assert.True(InteriorFrac(mask, floorStart, floorCount) < 0.1f,
            $"the notch floor is open to the sky and must read as visible skin, " +
            $"but {InteriorFrac(mask, floorStart, floorCount) * 100:F0}% of it read as interior");
    }

    [Fact]
    public void EverythingScopeIsUnaffected()
    {
        // Guards the default: a part with no scope set must not start losing pattern.
        var moves = new List<ToolpathMove>(Ring(0f, 0f, 200f, 240));
        moves.AddRange(Ring(0f, 0f, 80f, 120));
        foreach (var m in moves)
            Assert.False(SkinOnlyBracing.IsStructure(m, PatternScope.Everything));
    }

    [Fact]
    public void VisibleSkinWithoutAMaskTreatsNothingAsStructure()
    {
        // Fail-open, not fail-silent: if a caller ever forgets to build the mask the pattern
        // still lands rather than the part coming out silently smooth.
        var moves = Ring(0f, 0f, 200f, 240);
        foreach (var m in moves)
            Assert.False(SkinOnlyBracing.IsStructure(m, PatternScope.VisibleSkin));
    }

    [Fact]
    public void HomogenizeSettlesEachContourOneWay()
    {
        var outer = Ring(0f, 0f, 200f, 240);
        var inner = Ring(0f, 0f, 80f, 120);
        var moves = new List<ToolpathMove>(outer);
        moves.Add(new ToolpathMove(outer[^1].To, inner[0].From, MoveKind.Travel));
        moves.AddRange(inner);

        var mask = Mask(moves);
        SkinRaycastVisibility.HomogenizeByContour(moves, mask);

        // Each extrude run must now be uniform — no loop left half waved.
        for (int i = 1; i < outer.Count; i++)
            Assert.Equal(mask[0], mask[i]);
        int s = outer.Count + 1;
        for (int i = s + 1; i < moves.Count; i++)
            Assert.Equal(mask[s], mask[i]);

        Assert.False(mask[0]);       // outer ring patterned
        Assert.True(mask[s]);        // inner ring straight
    }
}
