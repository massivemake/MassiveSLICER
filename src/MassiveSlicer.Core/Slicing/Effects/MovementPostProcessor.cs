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

                if (move.Kind != MoveKind.Travel)
                {
                    newLayer.Moves.Add(move);
                    continue;
                }

                var hopStart = move.From;
                if (doWipe && lastExtrude is not null && !lastExtrude.IsLayerStitch)
                {
                    var wipeMoves = BuildWipeMoves(move.From, lastExtrude, settings).ToList();
                    foreach (var wipe in wipeMoves)
                        newLayer.Moves.Add(wipe);
                    if (wipeMoves.Count > 0)
                        hopStart = wipeMoves[^1].To;
                }

                if (doZHop)
                {
                    var hopTravel = hopStart == move.From ? move : move with { From = hopStart };
                    foreach (var hop in ExpandZHop(hopTravel, settings.ZHopMm))
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
        float ramp  = settings.WipeRampMm;
        float fullLen;
        float rampLen;

        if (ramp >= 0f)
        {
            rampLen = Math.Min(ramp, total);
            fullLen = total - rampLen;
        }
        else
        {
            // e.g. 35 mm same-direction + -1.5 mm ramp → 35 mm full extrusion, then 1.5 mm squeeze-down.
            fullLen = total;
            rampLen = -ramp;
        }

        var pos  = travelStart;
        var norm = lastExtrude.Normal;

        if (fullLen > 0.01f)
        {
            var next = pos + dir * fullLen;
            yield return new ToolpathMove(pos, next, MoveKind.Extrude)
            {
                IsWipe       = true,
                WipeRpmScale = 1f,
                Normal       = norm,
            };
            pos = next;
        }

        if (rampLen > 0.01f)
        {
            float segLen = rampLen / WipeRampSegments;
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