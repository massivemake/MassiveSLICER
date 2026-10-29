using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Colours a bead by how thick its layer is, so adaptive layer height is visible.
/// One score per move, in layer order. 0 is the thinnest cut in this toolpath,
/// 1 is the thickest. A path whose cuts are all the same thickness scores 0.5.
/// Travels score 0 and do not set the range. This does not move a vertex.
/// </summary>
public static class LayerHeightHeatmap
{
    public static float[] Scores(Toolpath toolpath)
    {
        int total = 0;
        foreach (var layer in toolpath.Layers)
            total += layer.Moves.Count;
        var scores = new float[total];
        if (total == 0) return scores;

        var thick = new float[total];
        float min = float.MaxValue;
        float max = float.MinValue;
        int i = 0;
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer = toolpath.Layers[li];
            var prev = li > 0 ? toolpath.Layers[li - 1] : null;
            var next = li + 1 < toolpath.Layers.Count ? toolpath.Layers[li + 1] : null;
            float nominal = NominalMm(layer, prev, next);
            foreach (var move in layer.Moves)
            {
                if (ToolpathMoveKinds.IsCutSegment(move.Kind))
                {
                    float scale = move.HeightScale;
                    if (float.IsNaN(scale) || scale <= 0f) scale = 1f;
                    float h = nominal * scale;
                    if (!float.IsNaN(h) && h > 0.0001f)
                    {
                        thick[i] = h;
                        if (h < min) min = h;
                        if (h > max) max = h;
                    }
                }
                i++;
            }
        }

        if (min == float.MaxValue) return scores;

        bool flat = max - min <= 0.001f;
        for (int k = 0; k < total; k++)
        {
            if (thick[k] <= 0.0001f) continue;
            scores[k] = flat ? 0.5f : (thick[k] - min) / (max - min);
        }
        return scores;
    }

    /// <summary>
    /// Layer thickness in mm. Uses <see cref="ToolpathLayer.Height"/> when the
    /// slicer stored it. Otherwise the gap to the previous layer, then the next.
    /// </summary>
    public static float NominalMm(ToolpathLayer layer, ToolpathLayer? previous, ToolpathLayer? next)
    {
        float h = layer.Height;
        if (!float.IsNaN(h) && h > 0.0001f) return h;

        if (previous is not null)
        {
            float d = MathF.Abs(layer.Z - previous.Z);
            if (!float.IsNaN(d) && d > 0.0001f) return d;
        }
        if (next is not null)
        {
            float d = MathF.Abs(next.Z - layer.Z);
            if (!float.IsNaN(d) && d > 0.0001f) return d;
        }
        return 0f;
    }
}
