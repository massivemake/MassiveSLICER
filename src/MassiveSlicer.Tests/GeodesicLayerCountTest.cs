using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Regression guard for geodesic layer counts: on a simple flat-bottomed prism the
/// distance field must span ≈ the part height (plus top-face spread), so the layer
/// count stays ≈ height / layerHeight — never an order of magnitude more.
/// </summary>
public sealed class GeodesicLayerCountTest
{
    /// <summary>Builds a closed triangulated box (soup) of the given size.</summary>
    static Vector3[] BoxSoup(float sx, float sy, float sz)
    {
        var p = new Vector3[8]
        {
            new(0, 0, 0),  new(sx, 0, 0),  new(sx, sy, 0),  new(0, sy, 0),
            new(0, 0, sz), new(sx, 0, sz), new(sx, sy, sz), new(0, sy, sz),
        };
        int[][] quads =
        [
            [3, 2, 1, 0], // bottom (normal -Z)
            [4, 5, 6, 7], // top (+Z)
            [0, 1, 5, 4], // front
            [1, 2, 6, 5], // right
            [2, 3, 7, 6], // back
            [3, 0, 4, 7], // left
        ];
        var soup = new List<Vector3>(quads.Length * 6);
        foreach (var q in quads)
        {
            soup.Add(p[q[0]]); soup.Add(p[q[1]]); soup.Add(p[q[2]]);
            soup.Add(p[q[0]]); soup.Add(p[q[2]]); soup.Add(p[q[3]]);
        }
        return [.. soup];
    }

    [Fact]
    public void Geodesic_Box_LayerCountMatchesHeight()
    {
        // 150×150×60 box at 3 mm layers: distance field spans 60 (walls) up to
        // ~60+150 (across the top face) — layer count must sit in that window.
        var soup = BoxSoup(150f, 150f, 60f);
        var settings = new SliceSettings { LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = 6f };

        var tp = GeodesicSlicer.Slice([soup], settings);

        Assert.InRange(tp.Layers.Count, 15, 80);        // ~20 expected; 605-style blowup fails
        int moves = tp.Layers.Sum(l => l.Moves.Count);
        Assert.InRange(moves / Math.Max(1, tp.Layers.Count), 1, 200); // sane per-layer density
    }

    [Fact]
    public void Geodesic_Box_ProgressReachesCompletion()
    {
        var soup = BoxSoup(100f, 100f, 30f);
        var settings = new SliceSettings { LayerHeight = 3f, FirstLayerHeight = 3f, BeadWidth = 6f };

        float last = -1f; int calls = 0;
        GeodesicSlicer.Slice([soup], settings, f => { last = f; calls++; });

        Assert.True(calls >= 3, $"expected phase callbacks, got {calls}");
        Assert.True(last >= 0.25f, $"progress should advance past the distance field, got {last}");
    }
}
