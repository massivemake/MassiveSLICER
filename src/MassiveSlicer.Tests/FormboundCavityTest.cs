using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Internal overhangs over MODELED cavities (real hollow parts, not shells-only
/// solids): a deck disc floating inside a hollow cylinder's void. The planner must
/// classify demand over the region HOLE as Cavity (mandatory support — never gated
/// by the sacrificial-fins setting), grow buttress columns through the void from
/// the cavity wall, and pick the island up with an umbilical connector so no
/// travel ever starts it.
/// </summary>
public sealed class FormboundCavityTest
{
    private const float LayerH = 3f, Bead = 6f;
    private static readonly float MaxStep = MathF.Min(LayerH * MathF.Tan(30f * MathF.PI / 180f), Bead * 0.5f);

    /// <summary>Closed hollow cylinder: outer skin r=80, inner skin r=70, annular
    /// caps — the interior is a REAL modeled void (region hole), unlike the
    /// solid-modeled vessels in the other tests.</summary>
    private static void HollowCylinder(List<Vector3> tris, float rOut, float rIn, float h, int seg = 64)
    {
        void Skin(float r, bool outward)
        {
            for (int i = 0; i < seg; i++)
            {
                float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
                var b0 = new Vector3(r * MathF.Cos(a0), r * MathF.Sin(a0), 0);
                var b1 = new Vector3(r * MathF.Cos(a1), r * MathF.Sin(a1), 0);
                var t0 = b0 with { Z = h };
                var t1 = b1 with { Z = h };
                if (outward) { tris.AddRange([b0, b1, t1]); tris.AddRange([b0, t1, t0]); }
                else         { tris.AddRange([b0, t1, b1]); tris.AddRange([b0, t0, t1]); }
            }
        }
        void Cap(float z, bool up)
        {
            for (int i = 0; i < seg; i++)
            {
                float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
                var i0 = new Vector3(rIn * MathF.Cos(a0), rIn * MathF.Sin(a0), z);
                var i1 = new Vector3(rIn * MathF.Cos(a1), rIn * MathF.Sin(a1), z);
                var o0 = new Vector3(rOut * MathF.Cos(a0), rOut * MathF.Sin(a0), z);
                var o1 = new Vector3(rOut * MathF.Cos(a1), rOut * MathF.Sin(a1), z);
                if (up) { tris.AddRange([i0, i1, o1]); tris.AddRange([i0, o1, o0]); }
                else    { tris.AddRange([i0, o1, i1]); tris.AddRange([i0, o0, o1]); }
            }
        }
        Skin(rOut, outward: true);
        Skin(rIn, outward: false);
        Cap(0f, up: false);
        Cap(h, up: true);
    }

    /// <summary>Solid disc (cylinder with caps) — the deck island in the void.</summary>
    private static void Disc(List<Vector3> tris, float r, float z0, float z1, int seg = 48)
    {
        for (int i = 0; i < seg; i++)
        {
            float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
            var b0 = new Vector3(r * MathF.Cos(a0), r * MathF.Sin(a0), z0);
            var b1 = new Vector3(r * MathF.Cos(a1), r * MathF.Sin(a1), z0);
            var t0 = b0 with { Z = z1 };
            var t1 = b1 with { Z = z1 };
            tris.AddRange([b0, b1, t1]);
            tris.AddRange([b0, t1, t0]);
            tris.AddRange([new Vector3(0, 0, z0), b1, b0]);
            tris.AddRange([new Vector3(0, 0, z1), t0, t1]);
        }
    }

    private static Vector3[] HollowCylinderWithDeck()
    {
        var tris = new List<Vector3>();
        HollowCylinder(tris, rOut: 80f, rIn: 70f, h: 200f);
        Disc(tris, r: 55f, z0: 100f, z1: 106f);   // island: 15 mm gap to the inner wall
        return [.. tris];
    }

    private static Toolpath SliceIt() => PlanarSlicer.Slice([HollowCylinderWithDeck()], new SliceSettings
    {
        LayerHeight = LayerH, FirstLayerHeight = LayerH, BeadWidth = Bead,
        InfillPattern = InfillPattern.FormboundButtress,
        LightningOverhangDeg = 30f,
        LightningAnchorInterior = true, LightningAnchorExterior = true,
        LightningExteriorOverhangs = false,   // cavity support must NOT depend on this
    }, null);

    [Fact]
    public void DeckOverTheVoidIsFullySupportedAndPicksUpWithoutTravels()
    {
        var tp = SliceIt();
        Assert.True(tp.Layers.Count > 50, $"only {tp.Layers.Count} layers");

        // 1. The deck actually prints: extrudes near its rim on its own layers.
        var deckLayers = tp.Layers.Where(l => l.Z is > 100f and < 107f).ToList();
        Assert.True(deckLayers.Count >= 1, "no deck layers sliced");
        foreach (var l in deckLayers)
            Assert.Contains(l.Moves, m => m.Kind == MoveKind.Extrude
                && MathF.Abs(new Vector2(m.From.X, m.From.Y).Length() - 52f) < 4f);

        // 2. No travel ever STARTS the island: layers gain no travels over the
        //    plain annulus baseline (one outer→inner wall hop). The island must be
        //    reached as a continuous detour from the cavity wall.
        int BaselineTravels(ToolpathLayer l) => l.Moves.Count(m =>
            m.Kind == MoveKind.Travel && !m.IsLayerChange && !m.IsZHop);
        int annulusMax = tp.Layers.Where(l => l.Z is > 20f and < 60f).Max(BaselineTravels);
        foreach (var l in deckLayers)
            Assert.True(BaselineTravels(l) <= annulusMax,
                $"z={l.Z:0.#}: island added travels ({BaselineTravels(l)} > baseline {annulusMax})");

        // 3. Support columns exist in the void: cavity extrudes between the inner
        //    wall and the deck rim on layers leading up to the deck.
        bool columns = tp.Layers.Where(l => l.Z is > 80f and < 100f)
            .SelectMany(l => l.Moves)
            .Any(m => m.Kind == MoveKind.Extrude
                && new Vector2((m.From.X + m.To.X) * 0.5f, (m.From.Y + m.To.Y) * 0.5f).Length() < 64f);
        Assert.True(columns, "no support columns grew through the cavity below the deck");

        // 4. THE invariant: nothing prints in mid-air. Every extrude midpoint sits
        //    within reach of the previous layer's material (overhang step + bead,
        //    with the bar-pitch bridging allowance the other suites use).
        float allowance = MathF.Max(MaxStep + Bead * 0.5f + 0.75f, 4f * Bead * 0.6f) + Bead * 0.5f;
        for (int li = 1; li < tp.Layers.Count; li++)
        {
            var below = tp.Layers[li - 1].Moves.Where(m => m.Kind == MoveKind.Extrude).ToList();
            if (below.Count == 0) continue;
            foreach (var m in tp.Layers[li].Moves)
            {
                if (m.Kind != MoveKind.Extrude || m.IsLayerStitch) continue;
                var mid = (m.From + m.To) * 0.5f;
                float best = float.MaxValue;
                foreach (var b in below)
                {
                    var ab = b.To - b.From;
                    float len2 = ab.LengthSquared();
                    float t = len2 < 1e-9f ? 0f : Math.Clamp(Vector3.Dot(mid - b.From, ab) / len2, 0f, 1f);
                    float d = (mid - (b.From + ab * t)).Length();
                    if (d < best) best = d;
                    if (best < allowance) break;
                }
                Assert.True(best <= allowance,
                    $"z={tp.Layers[li].Z:0.#}: bead at ({mid.X:0.#},{mid.Y:0.#}) floats {best:0.##} mm");
            }
        }

        // 5. Control: no phantom columns far below the deck's lead-in.
        bool strayLow = tp.Layers.Where(l => l.Z is > 5f and < 18f)
            .SelectMany(l => l.Moves)
            .Any(m => m.Kind == MoveKind.Extrude
                && new Vector2((m.From.X + m.To.X) * 0.5f, (m.From.Y + m.To.Y) * 0.5f).Length() < 60f);
        Assert.False(strayLow, "cavity columns grew where nothing above needs them");
    }
}
