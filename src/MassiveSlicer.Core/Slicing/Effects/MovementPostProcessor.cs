using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Inserts z-hop travel segments and pre-travel wipe extrusions after slicing.
/// </summary>
public static class MovementPostProcessor
{
    private const int WipeRampSegments = 4;

    public static Toolpath Apply(Toolpath toolpath, SliceSettings settings)
    {
        bool doZHop = settings.ZHopMm > 0f;
        bool doWipe = settings.WipeMode != WipeMode.None && settings.WipeLengthMm > 0f;
        if (!doZHop && !doWipe) return toolpath;

        var result = new Toolpath();
        foreach (var layer in toolpath.Layers)
        {
            var newLayer = new ToolpathLayer(layer.Index, layer.Z)
            {
                Height      = layer.Height,
                PlaneNormal = layer.PlaneNormal,
            };

            ToolpathMove? lastExtrude = null;
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude && !move.IsLayerStitch && !move.IsWipe)
                    lastExtrude = move;

                if (move.Kind == MoveKind.Travel && doWipe && lastExtrude is not null && !lastExtrude.IsLayerStitch)
                {
                    foreach (var wipe in BuildWipeMoves(move.From, lastExtrude, settings))
                        newLayer.Moves.Add(wipe);
                }

                if (move.Kind == MoveKind.Travel && doZHop)
                {
                    foreach (var hop in ExpandZHop(move, settings.ZHopMm))
                        newLayer.Moves.Add(hop);
                }
                else
                    newLayer.Moves.Add(move);
            }

            result.Layers.Add(newLayer);
        }

        return result;
    }

    private static IEnumerable<ToolpathMove> BuildWipeMoves(
        Vector3 travelStart, ToolpathMove lastExtrude, SliceSettings settings)
    {
        var delta = lastExtrude.To - lastExtrude.From;
        if (delta.LengthSquared() < 1e-6f) yield break;

        var dir = Vector3.Normalize(delta);
        if (settings.WipeMode == WipeMode.Retrace)
            dir = -dir;

        float total = settings.WipeLengthMm;
        float ramp  = Math.Clamp(settings.WipeRampMm, 0f, total);
        float full  = total - ramp;
        var   pos   = travelStart;
        var   norm  = lastExtrude.Normal;

        if (full > 0.01f)
        {
            var next = pos + dir * full;
            yield return new ToolpathMove(pos, next, MoveKind.Extrude)
            {
                IsWipe       = true,
                WipeRpmScale = 1f,
                Normal       = norm,
            };
            pos = next;
        }

        if (ramp > 0.01f)
        {
            float segLen = ramp / WipeRampSegments;
            for (int i = 0; i < WipeRampSegments; i++)
            {
                float scale = 1f - (i + 1) / (float)WipeRampSegments;
                var next = pos + dir * segLen;
                yield return new ToolpathMove(pos, next, MoveKind.Extrude)
                {
                    IsWipe       = true,
                    WipeRpmScale = scale,
                    Normal       = norm,
                };
                pos = next;
            }
        }
    }

    private static IEnumerable<ToolpathMove> ExpandZHop(ToolpathMove travel, float zHop)
    {
        var from = travel.From;
        var to   = travel.To;
        var liftFrom = new Vector3(from.X, from.Y, from.Z + zHop);
        var liftTo   = new Vector3(to.X, to.Y, to.Z + zHop);

        yield return new ToolpathMove(from, liftFrom, MoveKind.Travel)
        {
            IsZHop        = true,
            IsLayerChange = travel.IsLayerChange,
            Normal        = travel.Normal,
        };
        yield return new ToolpathMove(liftFrom, liftTo, MoveKind.Travel)
        {
            IsZHop        = true,
            IsLayerChange = false,
            Normal        = travel.Normal,
        };
        yield return new ToolpathMove(liftTo, to, MoveKind.Travel)
        {
            IsZHop        = true,
            IsLayerChange = false,
            Normal        = travel.Normal,
        };
    }
}