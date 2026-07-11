using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Lightning;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// X-bracing on an OPEN curved surface (Surface slicing mode, zig-zag single-skin)
/// — the production wavy-wall scenario. Guards the three failure modes that killed
/// the X on curved walls: parity ping-pong flipping with unstable chain orientation
/// (full-width travels), the cell-boundary merge trap (legs pinned to boundaries),
/// and the chord-projection fold under crests (march deadlocked at prev.S).
/// </summary>
public sealed class XBracingSurfaceReproTest
{
    private const float Bead = 6f;
    private const float Amp = 25f;
    private const float Wave = 150f;

    private static float SurfY(float x) => Amp * MathF.Sin(x * MathF.PI * 2f / Wave);

    /// <summary>Open wavy vertical sheet: y = A·sin(x·f), x∈[-300,300], z∈[0,150].</summary>
    private static Vector3[] WavySheet(
        float halfLen = 300f, float height = 150f, int nx = 60, int nz = 20)
    {
        var tris = new List<Vector3>();
        for (int i = 0; i < nx; i++)
        {
            float x0 = -halfLen + (2f * halfLen) * i / nx;
            float x1 = -halfLen + (2f * halfLen) * (i + 1) / nx;
            for (int k = 0; k < nz; k++)
            {
                float z0 = height * k / nz;
                float z1 = height * (k + 1) / nz;
                var a = new Vector3(x0, SurfY(x0), z0);
                var b = new Vector3(x1, SurfY(x1), z0);
                var c = new Vector3(x1, SurfY(x1), z1);
                var d = new Vector3(x0, SurfY(x0), z1);
                tris.AddRange([a, b, c]);
                tris.AddRange([a, c, d]);
            }
        }
        return [.. tris];
    }

    private static SliceSettings Settings() => new()
    {
        SlicingMode = SlicingMode.Surface,
        LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = Bead,
        InfillPattern = InfillPattern.None,
        ZigZagSeam = true,
        XBracingEnabled = true,
        XBracingDepthMm = 50f,
        XBracingSpanMm = 120f,
        XBracingAngleDeg = 30f,
        XBracingExtendEdges = true,
        LightningOverhangDeg = 45f,
    };

    [Fact]
    public void OpenSurfaceZigZagGetsHairpinsAndNeverTravels()
    {
        var tp = PlanarSlicer.Slice([WavySheet()], Settings(), null);
        Assert.True(tp.Layers.Count >= 10, $"expected many layers, got {tp.Layers.Count}");

        int layersWithHairpins = 0;
        float worstTravel = 0f;
        foreach (var lyr in tp.Layers)
        {
            int offSurfacePts = 0;
            foreach (var m in lyr.Moves)
            {
                if (m.Kind == MoveKind.Travel)
                {
                    float d = Vector2.Distance(
                        new Vector2(m.From.X, m.From.Y), new Vector2(m.To.X, m.To.Y));
                    worstTravel = MathF.Max(worstTravel, d);
                    continue;
                }
                if (m.IsLayerChange || m.IsLayerStitch) continue;
                if (MathF.Abs(m.To.Y - SurfY(m.To.X)) > Bead * 2f) offSurfacePts++;
            }
            if (offSurfacePts >= 2) layersWithHairpins++;
        }

        Assert.True(layersWithHairpins > tp.Layers.Count / 2,
            $"hairpins on {layersWithHairpins}/{tp.Layers.Count} layers");
        // Ping-pong must hold: no full-width fly-backs (canonical orientation + parity).
        Assert.True(worstTravel < Bead * 3f,
            $"zig-zag broke — travel of {worstTravel:0.#} mm (chain orientation flip?)");
    }

    [Fact]
    public void XDiagonalsMarchAcrossLayersAndCross()
    {
        var settings = Settings();
        var state = new XBracingPlanner.OpenPathDetourState();

        List<Vector2> WavyPath(bool reversed)
        {
            var p = new List<Vector2>();
            for (int i = 0; i <= 120; i++)
            {
                float x = -300f + 600f * i / 120f;
                p.Add(new Vector2(x, SurfY(x)));
            }
            if (reversed) p.Reverse();
            return p;
        }

        // cellH = span/tan(30°) ≈ 207.8 → idealDs ≈ 1.73 mm/layer.
        var uK0 = new List<float>();   // cell 0 leg A — marches +U
        var uK1 = new List<float>();   // cell 0 leg B — marches −U
        for (int li = 0; li < 49; li++)
        {
            float z = 3f + li * 3f;
            var contours = new List<List<Vector2>> { WavyPath(reversed: li % 2 == 1) };
            var closed = new List<bool> { false };
            XBracingPlanner.ApplyOpenPathDetours(
                contours, closed, z, 3f, settings, state, isBedLayer: li == 0);
            if (state.Prev.TryGetValue(0, out var h0)) uK0.Add(h0.S);
            if (state.Prev.TryGetValue(1, out var h1)) uK1.Add(h1.S);
        }

        Assert.True(uK0.Count > 40, $"leg A died: {uK0.Count} layers with a pin");
        Assert.True(uK1.Count > 40, $"leg B died: {uK1.Count} layers with a pin");

        // Diagonal march: leg A rises toward ~cellT·span, leg B falls to meet it.
        // Cell-boundary merge trap / projection folds froze this at < 5 mm before.
        float marchA = uK0[^1] - uK0[0];
        float marchB = uK1[0] - uK1[^1];
        Assert.True(marchA > 55f,
            $"leg A pinned — marched only {marchA:0.#} mm over {uK0.Count} layers (ideal ≈ 83)");
        Assert.True(marchB > 55f,
            $"leg B pinned — marched only {marchB:0.#} mm over {uK1.Count} layers (ideal ≈ 83)");

        // The legs of cell 0 must actually MEET (X cross) somewhere in the stack.
        float minSep = float.MaxValue;
        int n = Math.Min(uK0.Count, uK1.Count);
        for (int i = 0; i < n; i++)
            minSep = MathF.Min(minSep, MathF.Abs(uK0[i] - uK1[i]));
        Assert.True(minSep < Bead * 2.5f,
            $"diagonals never crossed — closest approach {minSep:0.#} mm");
    }
}
