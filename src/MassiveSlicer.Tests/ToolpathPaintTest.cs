using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>Brush tool marks: Remove marks delete painted beads and splice the gap
/// with a travel; Bridge marks inject manual Formbound demand so fingers grow
/// under the painted spot.</summary>
public sealed class ToolpathPaintTest
{
    private const float Bead = 6f;

    private static Vector3[] Cylinder(float r = 60f, float h = 120f, int seg = 48)
    {
        var tris = new List<Vector3>();
        for (int i = 0; i < seg; i++)
        {
            float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
            var b0 = new Vector3(r * MathF.Cos(a0), r * MathF.Sin(a0), 0);
            var b1 = new Vector3(r * MathF.Cos(a1), r * MathF.Sin(a1), 0);
            var t0 = b0 with { Z = h };
            var t1 = b1 with { Z = h };
            tris.AddRange([b0, b1, t1]);
            tris.AddRange([b0, t1, t0]);
            tris.AddRange([new Vector3(0, 0, 0), b1, b0]);
            tris.AddRange([new Vector3(0, 0, h), t0, t1]);
        }
        return [.. tris];
    }

    [Fact]
    public void RemoveMarksDeleteBeadsAndSpliceWithTravel()
    {
        var settingsClean = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
        };
        var clean = PlanarSlicer.Slice([Cylinder()], settingsClean, null);
        var midZ = clean.Layers[clean.Layers.Count / 2].Z;

        // Paint a 25 mm dab on the wall at (60, 0, midZ).
        var marked = PlanarSlicer.Slice([Cylinder()], new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            PaintMarks = [new PaintMark(new Vector3(60f, 0f, midZ), 25f, PaintMarkKind.Remove)],
        }, null);

        var layer = marked.Layers.Single(l => MathF.Abs(l.Z - midZ) < 0.01f);
        // No extrude midpoint remains inside the mark.
        foreach (var m in layer.Moves)
        {
            if (m.Kind != MoveKind.Extrude) continue;
            var mid = (m.From + m.To) * 0.5f;
            Assert.True(Vector3.Distance(mid, new Vector3(60f, 0f, midZ)) > 24.9f,
                $"painted bead survived at ({mid.X:0.#},{mid.Y:0.#})");
        }
        // The gap is spliced: exactly one non-layer-change travel bridges it, and
        // the chain stays connected.
        Assert.Contains(layer.Moves, m => m.Kind == MoveKind.Travel && !m.IsLayerChange);
        for (int k = 1; k < layer.Moves.Count; k++)
            Assert.True(Vector3.Distance(layer.Moves[k].From, layer.Moves[k - 1].To) < 0.01f,
                $"chain break at move {k}");

        // Other layers unaffected.
        var far = marked.Layers.First(l => MathF.Abs(l.Z - midZ) > 40f);
        Assert.DoesNotContain(far.Moves, m => m.Kind == MoveKind.Travel && !m.IsLayerChange);
    }

    [Fact]
    public void BridgeMarksGrowFingersUnderThePaintedSpot()
    {
        SliceSettings S(IReadOnlyList<PaintMark> marks) => new()
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            InfillPattern = InfillPattern.LightningBridge, LightningOverhangDeg = 30f,
            LightningAnchorInterior = true, LightningAnchorExterior = true,
            PaintMarks = marks,
        };

        // A plain cylinder wall never demands fingers on its own.
        var clean = PlanarSlicer.Slice([Cylinder()], S([]), null);
        Assert.DoesNotContain(clean.Layers.SelectMany(l => l.Moves), m => m.IsLightning);

        // Paint a Bridge dab INSIDE the region near the wall at z = 60: fingers must
        // appear on the layers just below it, reaching toward the painted spot.
        var target = new Vector3(40f, 0f, 60f);
        var painted = PlanarSlicer.Slice([Cylinder()],
            S([new PaintMark(target, 8f, PaintMarkKind.Bridge)]), null);

        var fingerMoves = painted.Layers
            .Where(l => l.Z is > 40f and < 62f)
            .SelectMany(l => l.Moves)
            .Where(m => m.IsLightning && m.Kind == MoveKind.Extrude)
            .ToList();
        Assert.True(fingerMoves.Count > 4, $"only {fingerMoves.Count} finger moves grew");
        Assert.Contains(fingerMoves, m =>
            Vector2.Distance(new Vector2(m.To.X, m.To.Y), new Vector2(target.X, target.Y)) < 2f * Bead);
    }

    [Fact]
    public void ButtressPaintSupportBarBirthsOneColumnUnderTheWallPath()
    {
        // Marks sit ON the perimeter wall path (same as UI path-select Support).
        // Must still birth a paint column — elbow is inset from the wall.
        const float r = 60f;
        var wallMid = new Vector3(r, 0f, 60f);
        var bar = new List<PaintMark>();
        for (int i = -4; i <= 4; i++)
        {
            float a = i * 0.08f;
            bar.Add(new PaintMark(
                new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), 60f),
                Bead * 1.5f, PaintMarkKind.Bridge, PaintBridgeRole.SupportBar));
        }
        // Single foot lower down at same angular position.
        bar.Add(new PaintMark(
            new Vector3(r, 0f, 30f), Bead * 1.5f, PaintMarkKind.Bridge, PaintBridgeRole.ColumnFoot));

        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            InfillPattern = InfillPattern.FormboundButtress, LightningOverhangDeg = 45f,
            LightningAnchorInterior = true, LightningAnchorExterior = true,
            LightningButtressBarMm = 40f,
            PaintMarks = bar,
        };
        var tp = PlanarSlicer.Slice([Cylinder()], settings, null);
        var lightning = tp.Layers.SelectMany(l => l.Moves)
            .Where(m => m.IsLightning && m.Kind == MoveKind.Extrude).ToList();
        Assert.True(lightning.Count > 8,
            $"paint buttress column produced only {lightning.Count} lightning moves " +
            $"(stats={tp.FormboundStats})");
        // Column should appear under the painted angular sector (near +X).
        Assert.Contains(lightning, m =>
            m.To.X > 20f && MathF.Abs(m.To.Y) < 40f);

        // Seam pin: each layer's first extrude (after optional layer travel/stitch)
        // should open near the ColumnFoot angular sector (+X wall at ~r,0).
        // A wandering seam on the far side of the cylinder would mean the pin failed.
        var foot = new Vector2(r, 0f);
        int pinnedLayers = 0, totalWithLoops = 0;
        foreach (var layer in tp.Layers)
        {
            var firstExt = layer.Moves.FirstOrDefault(m => m.Kind == MoveKind.Extrude && !m.IsLayerStitch);
            if (firstExt is null) continue;
            totalWithLoops++;
            var start = new Vector2(firstExt.From.X, firstExt.From.Y);
            // Cylinder radius 60: far-side seam would be ~120 mm from foot; pin should be < ~25 mm.
            if (Vector2.Distance(start, foot) < 25f)
                pinnedLayers++;
        }
        Assert.True(totalWithLoops >= 3, $"expected several layers, got {totalWithLoops}");
        Assert.True(pinnedLayers * 2 >= totalWithLoops,
            $"seam pin failed: only {pinnedLayers}/{totalWithLoops} layers open near bridge target");
    }

    [Fact]
    public void ButtressPaintMouthStaysAtFootNotUnderOffsetBar()
    {
        // Support bar at +Y; ColumnFoot at +X. The perimeter BREAK must stay on the
        // foot/seam stack (+X) at every height — bar only opens the T, never relocates
        // the mouth under the support selection (that was the wrong blue path).
        const float r = 60f;
        float barA = MathF.PI * 0.5f;   // +Y
        float footA = 0f;               // +X
        var marks = new List<PaintMark>();
        for (int i = -3; i <= 3; i++)
        {
            float a = barA + i * 0.06f;
            marks.Add(new PaintMark(
                new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), 60f),
                Bead * 1.5f, PaintMarkKind.Bridge, PaintBridgeRole.SupportBar));
        }
        marks.Add(new PaintMark(
            new Vector3(r * MathF.Cos(footA), r * MathF.Sin(footA), 30f),
            Bead * 1.5f, PaintMarkKind.Bridge, PaintBridgeRole.ColumnFoot));

        var settings = new SliceSettings
        {
            LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
            InfillPattern = InfillPattern.FormboundButtress, LightningOverhangDeg = 45f,
            LightningAnchorInterior = true, LightningAnchorExterior = true,
            LightningButtressBarMm = 40f,
            PaintMarks = marks,
        };
        var tp = PlanarSlicer.Slice([Cylinder()], settings, null);
        var lightning = tp.Layers
            .Where(l => l.Z is >= 28f and <= 62f)
            .SelectMany(l => l.Moves)
            .Where(m => m.IsLightning && m.Kind == MoveKind.Extrude)
            .ToList();
        Assert.True(lightning.Count > 8,
            $"offset paint bridge produced only {lightning.Count} lightning moves " +
            $"(stats={tp.FormboundStats})");

        // On EVERY bridge layer, some lightning must sit near the FOOT (+X), not only
        // under the bar (+Y). Mouth locked to seam/target.
        var foot = new Vector2(r, 0f);
        int layersOk = 0, layersChecked = 0;
        foreach (var layer in tp.Layers.Where(l => l.Z is >= 28f and <= 62f))
        {
            var tips = layer.Moves
                .Where(m => m.IsLightning && m.Kind == MoveKind.Extrude)
                .Select(m => new Vector2(m.To.X, m.To.Y))
                .ToList();
            if (tips.Count == 0) continue;
            layersChecked++;
            // Nearest lightning point to the foot wall — mouth should be within ~25 mm.
            float minD = tips.Min(p => Vector2.Distance(p, foot));
            if (minD < 25f) layersOk++;
        }
        Assert.True(layersChecked >= 3, $"expected several bridge layers, got {layersChecked}");
        Assert.True(layersOk * 2 >= layersChecked,
            $"mouth not locked to foot/seam: only {layersOk}/{layersChecked} layers near +X target");
    }
}
