using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Spiral / vase mode: ramps each layer's closed contour along the slicing-plane normal
/// across its arc length, so the print becomes one uninterrupted helix — no stepped
/// layer seam. On a horizontal slice that ramp is world Z. On an angled slice the
/// contour stays on its tilted plane and steps toward the next one.
/// Runs after <see cref="PatternEffect"/> (patterns are already finely segmented, so
/// the ramp stays smooth). Only closed chains spiral; open chains (surface-mode walls
/// that don't loop) keep their slice position, as a height ramp would tear their ends.
/// </summary>
public static class SpiralizeEffect
{
    public static Toolpath Apply(Toolpath toolpath, SliceSettings settings)
    {
        if (!settings.Spiralize || toolpath.Layers.Count == 0) return toolpath;

        // A pattern moves a loop's two ends apart even though they are the same vertex,
        // so "did this loop close" has to be asked at the pattern's own scale — a fixed
        // 1 mm reads a patterned loop as open and leaves the whole layer flat.
        float reach   = MathF.Max(0f, settings.PatternAmplitude)
                      + MathF.Max(0f, settings.EffectorStrengthMm);
        float closeTol = MathF.Max(1f, 2f * reach);
        float closeTolSq = closeTol * closeTol;

        var result = new Toolpath();
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer = toolpath.Layers[li];
            // Ramp along the slicing plane's normal by the gap to the next plane.
            // Layer.Z is only a label: planar stores the layer's world Z, angled stores
            // the plane's Z at the world origin (planeD / n.Z). Writing that label back
            // onto every point flattens a tilted contour onto a horizontal line and
            // walks each layer off the part. Measure the gap from the points themselves
            // (n · p). On a horizontal layer that is still next.Z - this.Z.
            var n = UnitNormal(layer);
            float along = li + 1 < toolpath.Layers.Count
                ? MeanPlaneDistance(toolpath.Layers[li + 1], n) - MeanPlaneDistance(layer, n)
                : layer.Height;
            if (along < 0f) along = 0f;

            var newLayer = new ToolpathLayer(layer.Index, layer.Z)
            {
                Height      = layer.Height,
                PlaneNormal = layer.PlaneNormal,
            };
            newLayer.Contours.AddRange(layer.Contours);

            if (along <= 0f)
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
                    && Vector3.DistanceSquared(moves[start].From, moves[j - 1].To) <= closeTolSq;

                if (!closed || total <= 1f)
                {
                    for (int k = start; k < j; k++) newLayer.Moves.Add(moves[k]);
                }
                else
                {
                    // Ramp along the plane normal with arc length. A horizontal layer
                    // still rises in world Z. A tilted layer stays on its plane and
                    // steps toward the next one, instead of being dropped onto layer.Z.
                    float cum = 0f;
                    for (int k = start; k < j; k++)
                    {
                        var mv  = moves[k];
                        float len = Vector3.Distance(mv.From, mv.To);
                        float f0  = cum / total;
                        float f1  = (cum + len) / total;
                        cum += len;
                        newLayer.Moves.Add(mv with
                        {
                            From = mv.From + n * (along * f0),
                            To   = mv.To   + n * (along * f1),
                        });
                    }
                }
                i = Math.Max(j, i + 1);
            }
            result.Layers.Add(newLayer);
        }
        RejoinConnectors(result);
        return result;
    }

    static Vector3 UnitNormal(ToolpathLayer layer)
    {
        var n = layer.PlaneNormal;
        float len2 = n.LengthSquared();
        return len2 < 1e-8f ? Vector3.UnitZ : n / MathF.Sqrt(len2);
    }

    /// <summary>Mean signed distance of this layer's extrude mids from the origin, along <paramref name="n"/>.</summary>
    static float MeanPlaneDistance(ToolpathLayer layer, Vector3 n)
    {
        double sum = 0;
        int count = 0;
        foreach (var m in layer.Moves)
        {
            if (m.Kind != MoveKind.Extrude || m.IsLayerStitch) continue;
            var mid = (m.From + m.To) * 0.5f;
            sum += Vector3.Dot(mid, n);
            if (++count >= 24) break;
        }
        return count > 0 ? (float)(sum / count) : layer.Z * n.Z;
    }

    /// <summary>
    /// Re-anchors the connector moves that join one layer to the next.
    /// <see cref="ToolpathLayerConnect"/> builds each stitch from the previous layer's
    /// end as it stood at slice time — flat, at that layer's own Z. Ramping lifts that
    /// end by a layer height, which leaves the stitch starting a full layer height below
    /// where the nozzle actually is: the tool drops back down and re-climbs, and the wall
    /// shows a gap at every layer change. Connectors exist purely to join, so each one is
    /// pulled onto the point the previous move actually ended at. A stitch left with
    /// nowhere to go is dropped rather than emitted as a zero-length bead.
    /// </summary>
    private static void RejoinConnectors(Toolpath toolpath)
    {
        Vector3? end = null;
        foreach (var layer in toolpath.Layers)
        {
            for (int i = 0; i < layer.Moves.Count; i++)
            {
                var m = layer.Moves[i];
                bool connector = m.IsLayerStitch || m.IsLayerChange || m.Kind == MoveKind.Travel;
                if (connector && end is Vector3 from && Vector3.DistanceSquared(m.From, from) > 1e-8f)
                {
                    if (Vector3.DistanceSquared(from, m.To) <= 1e-6f)
                    {
                        layer.Moves.RemoveAt(i--);   // the ramp already delivered the nozzle here
                        continue;
                    }
                    m = m with { From = from };
                    layer.Moves[i] = m;
                }
                end = m.To;
            }
        }
    }
}
