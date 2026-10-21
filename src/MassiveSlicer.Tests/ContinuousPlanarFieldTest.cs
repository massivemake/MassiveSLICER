using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Continuous Planar Field keeps each mesh island separate. On one layer every
/// bead is printed once, and no bead crosses another.
/// </summary>
public sealed class ContinuousPlanarFieldTest
{
    static List<Vector2> Rect(float x0, float y0, float x1, float y1) =>
    [
        new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1),
    ];

    [Fact]
    public void ReversedOrOverlappingBeads_AreTheSameLine()
    {
        Assert.True(ContinuousPlanarField.OverlapsBead(new(0, 0), new(10, 0), new(10, 0), new(0, 0)));
        Assert.True(ContinuousPlanarField.OverlapsBead(new(0, 0), new(10, 0), new(4, 0), new(12, 0)));
        Assert.False(ContinuousPlanarField.OverlapsBead(new(0, 0), new(10, 0), new(10, 0), new(20, 0)));
        Assert.False(ContinuousPlanarField.OverlapsBead(new(0, 0), new(10, 0), new(0, 1), new(10, 1)));
    }

    [Fact]
    public void TwoIslands_StaySeparate()
    {
        var islands = ContinuousPlanarField.Build(
            [Rect(0, 0, 20, 20), Rect(30, 0, 50, 20)],
            spacing: 6f);

        Assert.True(islands.Count >= 2);
        AssertClean(islands);
        foreach (var line in islands)
        {
            for (int i = 1; i < line.Count; i++)
            {
                var a = line[i - 1];
                var b = line[i];
                bool crossesGap = (a.X < 25f && b.X > 25f) || (b.X < 25f && a.X > 25f);
                Assert.False(crossesGap, $"island path crosses the gap {a} -> {b}");
            }
        }
        var all = islands.SelectMany(l => l);
        Assert.Contains(all, p => p.X < 1f && p.Y < 1f);
        Assert.Contains(all, p => p.X > 49f && p.Y < 1f);
    }

    [Fact]
    public void Square_FillsInsideWithoutRetraceOrCross()
    {
        var islands = ContinuousPlanarField.Build([Rect(0, 0, 40, 40)], spacing: 8f);

        AssertClean(islands);
        var all = islands.SelectMany(l => l).ToList();
        Assert.Contains(all, p => p.X == 0f && p.Y == 0f);
        Assert.Contains(all, p => p.X == 40f && p.Y == 0f);
        Assert.Contains(all, p => p.X == 40f && p.Y == 40f);
        Assert.Contains(all, p => p.X == 0f && p.Y == 40f);
        Assert.True(Length(islands) > 160f, $"length {Length(islands)}");
        AssertFilled(islands, 4f, 36f, 4f, spacing: 8f, hole: null);
    }

    [Fact]
    public void Square_StitchesToTheClosestInfillLine()
    {
        float spacing = 8f;
        var islands = ContinuousPlanarField.Build([Rect(0, 0, 40, 40)], spacing);

        var line = Assert.Single(islands);
        int stitches = 0;
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var b = line[i];
            bool interior = a.X > 1f && a.X < 39f && a.Y > 1f && a.Y < 39f
                && b.X > 1f && b.X < 39f && b.Y > 1f && b.Y < 39f;
            if (!interior || MathF.Abs(a.Y - b.Y) < 0.1f) continue;
            float d = Vector2.Distance(a, b);
            Assert.True(d <= spacing * 1.25f, $"stitch {a} -> {b} is {d:0.0} mm, past the next line");
            stitches++;
        }
        Assert.True(stitches >= 2, "infill lines stay separate instead of stitching to the next one");
    }

    [Fact]
    public void Hole_StaysOpenAndThePathDoesNotCross()
    {
        var hole = new List<Vector2> { new(15, 15), new(15, 25), new(25, 25), new(25, 15) };
        var islands = ContinuousPlanarField.Build([Rect(0, 0, 40, 40), hole], spacing: 4f);

        AssertClean(islands);
        Assert.Contains(islands.SelectMany(l => l), p => p.X == 0f && p.Y == 0f);
        Assert.True(Length(islands) > 160f, $"length {Length(islands)}");
        foreach (var line in islands)
        {
            for (int i = 1; i < line.Count; i++)
            {
                var mid = (line[i - 1] + line[i]) * 0.5f;
                bool throughHole = mid.X > 15.5f && mid.X < 24.5f && mid.Y > 15.5f && mid.Y < 24.5f;
                Assert.False(throughHole, $"segment cuts the hole {line[i - 1]} -> {line[i]}");
            }
        }
        AssertFilled(islands, 2f, 38f, 4f, spacing: 4f,
            hole: p => p.X > 15.5f && p.X < 24.5f && p.Y > 15.5f && p.Y < 24.5f);
    }

    static List<Vector2> Circle(float radius, int count, float phase)
    {
        var loop = new List<Vector2>(count);
        for (int i = 0; i < count; i++)
        {
            float a = phase + i * MathF.Tau / count;
            loop.Add(new Vector2(radius * MathF.Cos(a), radius * MathF.Sin(a)));
        }
        return loop;
    }

    static bool InHole(Vector2 p) => p.X * p.X + p.Y * p.Y < 9.6f * 9.6f;

    [Fact]
    public void TravelAcrossAHole_WalksTheWall()
    {
        var outer = Circle(20f, 64, 0f);
        var hole = Circle(10f, 32, 0f);
        var route = ContinuousPlanarField.TravelAround(
            new Vector2(20f, 0f), new Vector2(-20f, 0f), [outer, hole]);

        Assert.True(route.Count > 2, "the straight chord is replaced by a walk");
        for (int i = 1; i < route.Count; i++)
        {
            var mid = (route[i - 1] + route[i]) * 0.5f;
            Assert.False(InHole(mid), $"travel crosses the hole {route[i - 1]} -> {route[i]}");
        }
    }

    [Fact]
    public void Donut_NoSegmentCrossesTheHole()
    {
        var islands = ContinuousPlanarField.Build(
            [Circle(20f, 48, 0f), Circle(10f, 48, 0f)],
            spacing: 3f);

        AssertClean(islands);
        Assert.Contains(islands.SelectMany(l => l), p => MathF.Abs(p.Length() - 20f) < 0.5f);
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.Tau / 8f;
            var p = new Vector2(15f * MathF.Cos(a), 15f * MathF.Sin(a));
            float d = DistToPath(islands, p);
            Assert.True(d <= 4f, $"ring unfilled at {p}, {d:0.0} mm from the path");
        }
        foreach (var line in islands)
        {
            for (int i = 1; i < line.Count; i++)
            {
                var mid = (line[i - 1] + line[i]) * 0.5f;
                Assert.False(InHole(mid), $"crosses the hole {line[i - 1]} -> {line[i]}");
            }
        }
    }

    [Fact]
    public void Donut_NextLayerStartsAboveThePreviousEnd()
    {
        var mesh = AnnulusMesh(inner: 10f, outer: 20f, height: 12f, sides: 24);
        var tp = ContinuousPlanarSlicer.Slice([mesh], new SliceSettings
        {
            LayerHeight = 4f,
            FirstLayerHeight = 4f,
            BeadWidth = 3f,
        });

        Assert.True(tp.Layers.Count >= 2);
        for (int i = 0; i < tp.Layers.Count; i++)
        {
            var extrude = tp.Layers[i].Moves.Where(m => m.Kind == MoveKind.Extrude).ToList();
            Assert.NotEmpty(extrude);
            foreach (var move in extrude)
            {
                var mid = (move.From + move.To) * 0.5f;
                Assert.False(InHole(new Vector2(mid.X, mid.Y)),
                    $"layer {i} crosses the hole {move.From} -> {move.To}");
            }
            AssertMovesClean(tp.Layers[i]);
            if (i == 0) continue;
            var prev = tp.Layers[i - 1].Moves[^1].To;
            var start = extrude[0].From;
            float hop = Vector2.Distance(new Vector2(prev.X, prev.Y), new Vector2(start.X, start.Y));
            Assert.True(hop < 8f, $"layer {i} climbs {hop:0.0} mm away, across the opening");
            var hopMid = new Vector2((prev.X + start.X) * 0.5f, (prev.Y + start.Y) * 0.5f);
            Assert.False(InHole(hopMid), "the climb to the next layer crosses the hole");
        }
    }

    static Vector3[] AnnulusMesh(float inner, float outer, float height, int sides)
    {
        Vector3 Ring(int i, float r, float z)
        {
            float a = i * MathF.Tau / sides;
            return new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
        }
        var soup = new List<Vector3>();
        void Tri(Vector3 a, Vector3 b, Vector3 c) { soup.Add(a); soup.Add(b); soup.Add(c); }
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            Tri(Ring(i, outer, 0), Ring(j, outer, 0), Ring(j, outer, height));
            Tri(Ring(i, outer, 0), Ring(j, outer, height), Ring(i, outer, height));
            Tri(Ring(i, inner, 0), Ring(i, inner, height), Ring(j, inner, height));
            Tri(Ring(i, inner, 0), Ring(j, inner, height), Ring(j, inner, 0));
            Tri(Ring(i, outer, height), Ring(j, outer, height), Ring(j, inner, height));
            Tri(Ring(i, outer, height), Ring(j, inner, height), Ring(i, inner, height));
            Tri(Ring(i, outer, 0), Ring(i, inner, 0), Ring(j, inner, 0));
            Tri(Ring(i, outer, 0), Ring(j, inner, 0), Ring(j, outer, 0));
        }
        return [.. soup];
    }

    [Fact]
    public void Box_EachLayerPrintsEachBeadOnce()
    {
        var soup = Box(30f, 30f, 12f);
        var tp = ContinuousPlanarSlicer.Slice([soup], new SliceSettings
        {
            LayerHeight = 4f,
            FirstLayerHeight = 4f,
            BeadWidth = 6f,
        });

        Assert.InRange(tp.Layers.Count, 2, 5);
        foreach (var layer in tp.Layers)
        {
            var extrude = layer.Moves.Where(m => m.Kind == MoveKind.Extrude).ToList();
            Assert.True(extrude.Count >= 4, $"layer {layer.Index} extrudes {extrude.Count}");
            AssertMovesClean(layer);
            Assert.All(extrude, m => Assert.Equal(layer.Z, m.From.Z, 3));
            Assert.All(extrude, m => Assert.Equal(layer.Z, m.To.Z, 3));
        }
    }

    static void AssertFilled(IReadOnlyList<List<Vector2>> lines, float min, float max, float step,
                             float spacing, Func<Vector2, bool>? hole)
    {
        float limit = spacing * 0.75f;
        for (float y = min; y <= max; y += step)
        for (float x = min; x <= max; x += step)
        {
            var p = new Vector2(x, y);
            if (hole is not null && hole(p)) continue;
            float d = DistToPath(lines, p);
            Assert.True(d <= limit, $"unfilled {p} is {d:0.0} mm from the path");
        }
    }

    static float DistToPath(IReadOnlyList<List<Vector2>> lines, Vector2 p)
    {
        float best = float.MaxValue;
        foreach (var line in lines)
            for (int i = 1; i < line.Count; i++)
            {
                float d = DistToSeg(p, line[i - 1], line[i]);
                if (d < best) best = d;
            }
        return best;
    }

    static float DistToSeg(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-8f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    static float Length(IEnumerable<List<Vector2>> lines)
    {
        float length = 0f;
        foreach (var line in lines)
            for (int i = 1; i < line.Count; i++)
                length += Vector2.Distance(line[i - 1], line[i]);
        return length;
    }

    /// <summary>Same-layer rule: no bead repeats, in either direction, and no bead crosses another.</summary>
    static void AssertClean(IReadOnlyList<List<Vector2>> lines)
    {
        var segs = new List<(Vector2 A, Vector2 B)>();
        foreach (var line in lines)
            for (int i = 1; i < line.Count; i++)
                segs.Add((line[i - 1], line[i]));

        for (int i = 0; i < segs.Count; i++)
        for (int j = i + 1; j < segs.Count; j++)
        {
            var (a, b) = segs[i];
            var (c, d) = segs[j];
            Assert.False(ContinuousPlanarField.OverlapsBead(a, b, c, d),
                $"retraces {a} -> {b} and {c} -> {d}");
            Assert.False(ContinuousPlanarField.ProperCross(a, b, c, d),
                $"crosses {a} -> {b} and {c} -> {d}");
        }
    }

    static void AssertMovesClean(ToolpathLayer layer)
    {
        var extrude = layer.Moves.Where(m => m.Kind == MoveKind.Extrude).ToList();
        for (int i = 0; i < extrude.Count; i++)
        for (int j = i + 1; j < extrude.Count; j++)
        {
            var a = new Vector2(extrude[i].From.X, extrude[i].From.Y);
            var b = new Vector2(extrude[i].To.X, extrude[i].To.Y);
            var c = new Vector2(extrude[j].From.X, extrude[j].From.Y);
            var d = new Vector2(extrude[j].To.X, extrude[j].To.Y);
            Assert.False(ContinuousPlanarField.OverlapsBead(a, b, c, d),
                $"layer {layer.Index} retraces {extrude[i].From} -> {extrude[i].To}");
            Assert.False(ContinuousPlanarField.ProperCross(a, b, c, d),
                $"layer {layer.Index} crosses {extrude[i].From} -> {extrude[i].To}");
        }

        foreach (var move in layer.Moves)
        {
            if (move.Kind != MoveKind.Travel) continue;
            bool above = move.From.Z > layer.Z + 0.05f && move.To.Z > layer.Z + 0.05f;
            bool vertical = MathF.Abs(move.From.X - move.To.X) < 1e-3f
                && MathF.Abs(move.From.Y - move.To.Y) < 1e-3f;
            Assert.True(above || vertical, $"layer {layer.Index} travels on the layer {move.From} -> {move.To}");
        }
    }

    static Vector3[] Box(float sx, float sy, float sz)
    {
        var p = new Vector3[]
        {
            new(0, 0, 0), new(sx, 0, 0), new(sx, sy, 0), new(0, sy, 0),
            new(0, 0, sz), new(sx, 0, sz), new(sx, sy, sz), new(0, sy, sz),
        };
        int[][] quads =
        [
            [3, 2, 1, 0], [4, 5, 6, 7], [0, 1, 5, 4],
            [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7],
        ];
        var soup = new List<Vector3>();
        foreach (var q in quads)
        {
            soup.Add(p[q[0]]); soup.Add(p[q[1]]); soup.Add(p[q[2]]);
            soup.Add(p[q[0]]); soup.Add(p[q[2]]); soup.Add(p[q[3]]);
        }
        return [.. soup];
    }
}
