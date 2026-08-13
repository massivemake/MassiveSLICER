using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// "Pattern outer skin only": decorative effects displace the printed skin while structure
/// (infill, X-bracing, Formbound fill, supports) stays straight — but structure ends still
/// follow the wall so braces stay bonded to it.
/// </summary>
public sealed class PatternSkinOnlyTest
{
    /// <summary>Square wall loop, tagged as skin, plus one straight brace across the middle.</summary>
    private static Toolpath WalledBoxWithBrace(float half = 100f, float z = 3f)
    {
        var layer = new ToolpathLayer(0, z);

        Vector3 P(float x, float y) => new(x, y, z);
        var corners = new[] { P(-half, -half), P(half, -half), P(half, half), P(-half, half) };

        // Skin: closed loop, subdivided so a wave has something to bite on.
        for (int c = 0; c < 4; c++)
        {
            var a = corners[c];
            var b = corners[(c + 1) % 4];
            const int Steps = 20;
            for (int s = 0; s < Steps; s++)
                layer.Moves.Add(new ToolpathMove(
                    Vector3.Lerp(a, b, s / (float)Steps),
                    Vector3.Lerp(a, b, (s + 1) / (float)Steps),
                    MoveKind.Extrude) { IsWall = true });
        }

        // Structure: travel in, then one brace spanning wall to wall. Untagged, like every
        // infill / X-bracing emitter produces.
        layer.Moves.Add(new ToolpathMove(corners[3], P(-half, 0f), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(P(-half, 0f), P(half, 0f), MoveKind.Extrude));

        var tp = new Toolpath();
        tp.Layers.Add(layer);
        return tp;
    }

    private static SliceSettings Wave(PatternScope scope) => new()
    {
        WaveEffect     = WaveEffectType.Sine,
        WaveAmplitude  = 8f,
        WaveWavelength = 60f,
        PatternScope   = scope,
    };

    /// <summary>How far a point sits off an axis-aligned square outline of the given half-size.</summary>
    private static float OffSquare(Vector3 p, float half)
        => MathF.Abs(MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)) - half);

    private static List<ToolpathMove> Structure(Toolpath tp) =>
        [.. tp.Layers[0].Moves.Where(m => m.Kind == MoveKind.Extrude && !m.IsWall)];

    private static List<ToolpathMove> Skin(Toolpath tp) =>
        [.. tp.Layers[0].Moves.Where(m => m.Kind == MoveKind.Extrude && m.IsWall)];

    [Fact]
    public void SkinOnlyLeavesTheBraceStraight()
    {
        var outp = WaveEffect.Apply(WalledBoxWithBrace(), Wave(PatternScope.WallsOnly));
        var brace = Structure(outp);
        Assert.NotEmpty(brace);

        // Every brace vertex must lie on the line through its two ends.
        var a = brace[0].From;
        var b = brace[^1].To;
        var dir = Vector3.Normalize(b - a);
        foreach (var m in brace)
            foreach (var p in new[] { m.From, m.To })
            {
                var v = p - a;
                float off = (v - dir * Vector3.Dot(v, dir)).Length();
                Assert.True(off < 0.05f, $"brace bowed by {off:F3}mm at {p}");
            }
    }

    [Fact]
    public void SkinOnlyStillMovesTheBraceEndsOntoTheDisplacedWall()
    {
        var outp  = WaveEffect.Apply(WalledBoxWithBrace(), Wave(PatternScope.WallsOnly));
        var brace = Structure(outp);

        var movedStart = (brace[0].From - new Vector3(-100f, 0f, 3f)).Length();
        var movedEnd   = (brace[^1].To  - new Vector3(100f, 0f, 3f)).Length();

        // If the ends did not move with the wall the brace would part from the skin by up to
        // the amplitude — a bonding failure, not a cosmetic one.
        Assert.True(movedStart > 0.01f || movedEnd > 0.01f,
            $"brace ends did not follow the wall (start moved {movedStart:F3}, end {movedEnd:F3})");
    }

    [Fact]
    public void SkinIsStillWavedWhenSkinOnlyIsOn()
    {
        var outp = WaveEffect.Apply(WalledBoxWithBrace(), Wave(PatternScope.WallsOnly));
        var skin = Skin(outp);
        Assert.NotEmpty(skin);

        // The bottom wall run sits at y = -100; a sine displaces it off that line.
        float maxOff = skin.Where(m => m.From.Y < -50f && m.To.Y < -50f)
                           .Select(m => MathF.Abs(m.From.Y + 100f))
                           .DefaultIfEmpty(0f)
                           .Max();
        Assert.True(maxOff > 1f, $"skin was not displaced (max {maxOff:F3}mm)");
    }

    [Fact]
    public void OffKeepsThePreviousBehaviourAndWavesTheBraceToo()
    {
        var outp  = WaveEffect.Apply(WalledBoxWithBrace(), Wave(PatternScope.Everything));
        var brace = Structure(outp);
        Assert.NotEmpty(brace);

        var a = brace[0].From;
        var b = brace[^1].To;
        var dir = Vector3.Normalize(b - a);
        float worst = 0f;
        foreach (var m in brace)
        {
            var v = m.From - a;
            worst = MathF.Max(worst, (v - dir * Vector3.Dot(v, dir)).Length());
        }

        Assert.True(worst > 1f,
            $"with skin-only OFF the brace should still be waved, but it deviated only {worst:F3}mm");
    }

    [Fact]
    public void WallTagSurvivesTheEffectSoLaterEffectsCanStillSeeTheSkin()
    {
        // WaveEffect runs before PatternEffect. If the rebuild dropped IsWall, the pattern
        // would find no skin and silently fall back to displacing everything.
        var outp = WaveEffect.Apply(WalledBoxWithBrace(), Wave(PatternScope.WallsOnly));
        Assert.Contains(outp.Layers[0].Moves, m => m.IsWall);
    }

    /// <summary>
    /// Outer skin plus an INTERIOR wall — a cavity boundary, which is what a modelled rib or
    /// brace slices into. Both are perimeters, so IsWall alone cannot separate them; only the
    /// nesting depth carried on IsOuterWall can.
    /// </summary>
    private static Toolpath BoxWithInteriorRibWall(float half = 100f, float z = 3f)
    {
        var layer = new ToolpathLayer(0, z);
        Vector3 P(float x, float y) => new(x, y, z);

        void Loop(Vector3[] pts, bool outer)
        {
            for (int c = 0; c < pts.Length; c++)
            {
                var a = pts[c];
                var b = pts[(c + 1) % pts.Length];
                const int Steps = 20;
                for (int st = 0; st < Steps; st++)
                    layer.Moves.Add(new ToolpathMove(
                        Vector3.Lerp(a, b, st / (float)Steps),
                        Vector3.Lerp(a, b, (st + 1) / (float)Steps),
                        MoveKind.Extrude) { IsWall = true, IsOuterWall = outer });
            }
        }

        Loop([P(-half, -half), P(half, -half), P(half, half), P(-half, half)], outer: true);
        layer.Moves.Add(new ToolpathMove(P(-half, half), P(-40f, -40f), MoveKind.Travel));
        Loop([P(-40f, -40f), P(40f, -40f), P(40f, 40f), P(-40f, 40f)], outer: false);

        var tp = new Toolpath();
        tp.Layers.Add(layer);
        return tp;
    }

    [Fact]
    public void OuterSurfaceOnlyLeavesAnInteriorWallStraight()
    {
        var outp = WaveEffect.Apply(BoxWithInteriorRibWall(), Wave(PatternScope.OuterSurfaceOnly));

        // The interior loop's bottom run sits at y = -40 and must stay there.
        float worst = outp.Layers[0].Moves
            .Where(m => m.Kind == MoveKind.Extrude && m.IsWall && !m.IsOuterWall)
            .Select(m => OffSquare(m.From, 40f))
            .DefaultIfEmpty(0f).Max();

        Assert.True(worst < 0.05f, $"interior wall was displaced by {worst:F3}mm");
    }

    [Fact]
    public void WallsOnlyStillTexturesAnInteriorWall()
    {
        // The discriminator: under WallsOnly an interior wall IS a wall, so it must still be
        // waved. If both modes behaved alike the new setting would be doing nothing.
        var outp = WaveEffect.Apply(BoxWithInteriorRibWall(), Wave(PatternScope.WallsOnly));

        float worst = outp.Layers[0].Moves
            .Where(m => m.Kind == MoveKind.Extrude && m.IsWall && !m.IsOuterWall)
            .Select(m => OffSquare(m.From, 40f))
            .DefaultIfEmpty(0f).Max();

        Assert.True(worst > 1f, $"interior wall should be waved under WallsOnly, moved {worst:F3}mm");
    }

    [Fact]
    public void OuterSurfaceOnlyStillTexturesTheOuterSkin()
    {
        var outp = WaveEffect.Apply(BoxWithInteriorRibWall(), Wave(PatternScope.OuterSurfaceOnly));

        float worst = outp.Layers[0].Moves
            .Where(m => m.Kind == MoveKind.Extrude && m.IsOuterWall)
            .Select(m => OffSquare(m.From, 100f))
            .DefaultIfEmpty(0f).Max();

        Assert.True(worst > 1f, $"outer skin was not displaced (max {worst:F3}mm)");
    }
}
