using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>Opportunistic regression check against the REAL 26-282 fuselage STL on
/// the shop NAS — the part that produced every angled-lightning field bug. Skips
/// silently when the share isn't mounted (CI, other machines).</summary>
public class FuselageRealCheck(ITestOutputHelper o)
{
    private const string Stl =
        "/Volumes/MassiveFILES/Projects/26-282 - Fuselage Caracol Lead/01-Pre Design/2026_0705 - Fuselage Simplified V06.stl";

    [Fact]
    public void RealFuselageMetrics()
    {
        if (!File.Exists(Stl)) return;   // NAS not mounted — nothing to check
        using var br = new BinaryReader(File.OpenRead(Stl));
        br.ReadBytes(80);
        uint n = br.ReadUInt32();
        var tris = new Vector3[n * 3];
        for (int i = 0; i < n; i++)
        {
            br.ReadBytes(12);
            for (int v = 0; v < 3; v++)
                tris[i * 3 + v] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            br.ReadBytes(2);
        }
        float minZ = float.MaxValue;
        for (int i = 0; i < tris.Length; i++) { tris[i] *= 1000f; minZ = MathF.Min(minZ, tris[i].Z); }
        for (int i = 0; i < tris.Length; i++) tris[i].Z -= minZ;

        SliceSettings S(InfillPattern p) => new()
        {
            LayerHeight = 2.5f, FirstLayerHeight = 2.5f, BeadWidth = 6f,
            TiltAngle = -35.5f, InfillPattern = p, LightningOverhangDeg = 30f,
            LightningAnchorInterior = true, LightningAnchorExterior = true,
        };
        var lightning = AngledPlanarSlicer.Slice([tris], S(InfillPattern.LightningBridge));
        var baseline  = AngledPlanarSlicer.Slice([tris], S(InfillPattern.None));

        var byZ = lightning.Layers.ToDictionary(l => MathF.Round(l.Z, 1));
        float worstGap = 0, worstZ = 0; int excessTravelLayers = 0;
        foreach (var bl in baseline.Layers)
        {
            if (!byZ.TryGetValue(MathF.Round(bl.Z, 1), out var ll)) continue;
            var segs = ll.Moves.Where(m => m.Kind == MoveKind.Extrude && !m.IsWipe).ToList();
            if (segs.Count == 0) continue;
            foreach (var bm in bl.Moves)
            {
                if (bm.Kind != MoveKind.Extrude || bm.IsWipe) continue;
                if (Vector3.Distance(bm.From, bm.To) < 1.5f) continue;
                var mid = (bm.From + bm.To) * 0.5f;
                float best = float.MaxValue;
                foreach (var s in segs)
                {
                    float d = Dist(mid, s.From, s.To);
                    if (d < best) best = d;
                    if (best < 8f) break;
                }
                if (best > worstGap && best < 1e9f) { worstGap = best; worstZ = bl.Z; }
            }
            int T(ToolpathLayer l) => l.Moves.Count(m => m.Kind == MoveKind.Travel && !m.IsLayerChange && !m.IsZHop);
            if (T(bl) <= 10 && T(ll) > T(bl) + Math.Max(2, T(bl) / 2)) excessTravelLayers++;
        }
        o.WriteLine($"worstGap={worstGap:0.#} at z={worstZ:0.#}  excessTravelLayers={excessTravelLayers}");
        Assert.True(worstGap < 95f, $"regressed: {worstGap:0.#} mm missing at z={worstZ:0.#}");
        Assert.True(excessTravelLayers < 12, $"regressed: {excessTravelLayers} fragmented layers");
    }

    private static float Dist(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-9f ? 0f : Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f);
        return (p - (a + ab * t)).Length();
    }
}
