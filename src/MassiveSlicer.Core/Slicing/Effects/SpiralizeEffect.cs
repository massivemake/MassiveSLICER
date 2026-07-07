using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Spiral / vase mode: ramps each layer's closed contour continuously in Z across its
/// arc length, so the print becomes one uninterrupted helix — no stepped layer seam.
/// Runs after <see cref="PatternEffect"/> (patterns are already finely segmented, so
/// the ramp stays smooth). Only closed chains spiral; open chains (surface-mode walls
/// that don't loop) keep their flat layer Z, as a height ramp would tear their ends.
/// </summary>
public static class SpiralizeEffect
{
    public static Toolpath Apply(Toolpath toolpath, SliceSettings settings)
    {
        if (!settings.Spiralize || toolpath.Layers.Count == 0) return toolpath;

        var result = new Toolpath();
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer = toolpath.Layers[li];
            float height = layer.Height > 0f
                ? layer.Height
                : (li + 1 < toolpath.Layers.Count
                    ? MathF.Max(0f, toolpath.Layers[li + 1].Z - layer.Z)
                    : 0f);

            var newLayer = new ToolpathLayer(layer.Index, layer.Z)
            {
                Height      = layer.Height,
                PlaneNormal = layer.PlaneNormal,
            };
            newLayer.Contours.AddRange(layer.Contours);

            if (height <= 0f)
            {
                foreach (var m in layer.Moves) newLayer.Moves.Add(m);
                result.Layers.Add(newLayer);
                continue;
            }

            int i = 0;
            var moves = layer.Moves;
            while (i < moves.Count)
            {
                var m = moves[i];
                if (m.Kind != MoveKind.Extrude || m.IsLayerStitch)
                {
                    newLayer.Moves.Add(m);
                    i++;
                    continue;
                }

                // Collect the contiguous extrude chain starting here.
                int start = i;
                float total = 0f;
                var prevTo = m.From;
                int j = i;
                while (j < moves.Count)
                {
                    var mv = moves[j];
                    if (mv.Kind != MoveKind.Extrude || mv.IsLayerStitch) break;
                    if (Vector3.DistanceSquared(mv.From, prevTo) > 1.0f) break;
                    total += Vector3.Distance(mv.From, mv.To);
                    prevTo = mv.To;
                    j++;
                }

                bool closed = total > 1f
                    && Vector3.DistanceSquared(moves[start].From, moves[j - 1].To) <= 1.0f;

                if (!closed || total <= 1f)
                {
                    for (int k = start; k < j; k++) newLayer.Moves.Add(moves[k]);
                }
                else
                {
                    // Ramp Z linearly with arc length: loop start at layer Z, loop end
                    // one layer height up — meeting the next layer's start exactly.
                    float cum = 0f;
                    for (int k = start; k < j; k++)
                    {
                        var mv  = moves[k];
                        float len = Vector3.Distance(mv.From, mv.To);
                        float z0  = layer.Z + height * (cum / total);
                        float z1  = layer.Z + height * ((cum + len) / total);
                        cum += len;
                        newLayer.Moves.Add(mv with
                        {
                            From = new Vector3(mv.From.X, mv.From.Y, z0),
                            To   = new Vector3(mv.To.X,   mv.To.Y,   z1),
                        });
                    }
                }
                i = Math.Max(j, i + 1);
            }
            result.Layers.Add(newLayer);
        }
        return result;
    }
}
