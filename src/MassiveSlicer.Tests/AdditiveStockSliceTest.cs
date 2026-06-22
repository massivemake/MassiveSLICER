using System;
using System.Linq;
using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>
/// Proves the additive "stock from maps" path: slicing the displaced surface (map detail +
/// uniform allowance) yields a blank that envelopes the raw part — taller and wider by the
/// allowance — so the later mill always has material. Mirrors what RunSliceAsync feeds the slicer.
/// </summary>
public class AdditiveStockSliceTest(ITestOutputHelper output)
{
    [Fact]
    public void DisplacedStock_IsTallerAndWiderThanRawMesh_ByAllowance()
    {
        var (pos, nrm, uv, idx) = IndexedBox(40f, 40f, 20f);
        var flat = new HeightField2D(Enumerable.Repeat(1f, 4).ToArray(), 2, 2);   // uniform "high"
        const float allowance = 4f;

        // Raw blank vs displaced blank (distance 0 -> pure allowance skin so the math is clean).
        var raw  = DisplacedSurfaceBuilder.Build(pos, nrm, uv, idx, flat, displacementDistance: 0f, extraOffsetMm: 0f);
        var disp = DisplacedSurfaceBuilder.Build(pos, nrm, uv, idx, flat, displacementDistance: 0f, extraOffsetMm: allowance);

        var settings = new SliceSettings
        {
            SlicingMode = SlicingMode.Surface, LayerHeight = 4f, FirstLayerHeight = 4f,
            BeadWidth = 6f, InfillPattern = InfillPattern.None,
        };
        var rawTp  = PlanarSlicer.Slice([Soup(raw)], settings);
        var dispTp = PlanarSlicer.Slice([Soup(disp)], settings);

        Assert.NotEmpty(rawTp.Layers);
        Assert.NotEmpty(dispTp.Layers);

        float rawTop  = rawTp.Layers.Max(l => l.Z);
        float dispTop = dispTp.Layers.Max(l => l.Z);
        float rawW  = MaxAbsX(rawTp);
        float dispW = MaxAbsX(dispTp);
        output.WriteLine($"raw: top={rawTop:F1} halfWidth={rawW:F1}   disp(+{allowance}mm): top={dispTop:F1} halfWidth={dispW:F1}");

        // The displaced blank stands taller and reaches wider than the raw part (allowance skin).
        Assert.True(dispTop > rawTop + 1f, $"stock should be taller: raw {rawTop} vs disp {dispTop}");
        Assert.True(dispW   > rawW   + 1f, $"stock should be wider: raw {rawW} vs disp {dispW}");
    }

    private static Vector3[] Soup(DisplacedSurfaceBuilder.Result r)
    {
        var soup = new Vector3[r.Indices.Length];
        for (int i = 0; i < soup.Length; i++) soup[i] = r.Positions[r.Indices[i]];
        return soup;
    }

    private static float MaxAbsX(Toolpath tp) =>
        tp.Layers.SelectMany(l => l.Moves)
          .SelectMany(m => new[] { MathF.Abs(m.From.X), MathF.Abs(m.To.X) })
          .DefaultIfEmpty(0f).Max();

    // 8-vertex closed box with outward corner normals + flat UVs (no subdivision).
    private static (Vector3[] pos, Vector3[] nrm, Vector2[] uv, int[] idx) IndexedBox(float w, float d, float h)
    {
        float hw = w * 0.5f, hd = d * 0.5f;
        Vector3[] pos =
        [
            new(-hw, -hd, 0), new(hw, -hd, 0), new(hw, hd, 0), new(-hw, hd, 0),
            new(-hw, -hd, h), new(hw, -hd, h), new(hw, hd, h), new(-hw, hd, h),
        ];
        var nrm = pos.Select(p => Vector3.Normalize(new Vector3(MathF.Sign(p.X == 0 ? -1 : p.X),
                                                                MathF.Sign(p.Y == 0 ? -1 : p.Y),
                                                                p.Z > 0 ? 1 : -1))).ToArray();
        var uv = Enumerable.Repeat(new Vector2(0.5f, 0.5f), 8).ToArray();
        int[] idx =
        [
            0,1,2, 0,2,3,   // bottom
            4,6,5, 4,7,6,   // top
            0,4,5, 0,5,1,   // y-
            1,5,6, 1,6,2,   // x+
            2,6,7, 2,7,3,   // y+
            3,7,4, 3,4,0,   // x-
        ];
        return (pos, nrm, uv, idx);
    }
}
