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
}
