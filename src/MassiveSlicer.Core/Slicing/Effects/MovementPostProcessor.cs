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
        bool doEnd = settings.EndRetractEnabled && settings.EndRetractDistanceMm > 0.05f;
        if (!doZHop && !doWipe && !doEnd) return toolpath;

        var result = new Toolpath { BeadWidthMm = toolpath.BeadWidthMm };
        // Keep the last real bead across layers so a layer-change travel
        // (first move of layer N+1) still gets smash + wipe. Resetting per
        // layer dropped every hop at the seam.
        ToolpathMove? prevExtrude = null;
        ToolpathMove? lastExtrude = null;
        var lastPlane = Vector3.UnitZ;
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            var layer = toolpath.Layers[li];
            var newLayer = new ToolpathLayer(layer.Index, layer.Z)
            {
                Height      = layer.Height,
                PlaneNormal = layer.PlaneNormal,
            };

            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                if (move.Kind == MoveKind.Extrude && !move.IsLayerStitch && !move.IsWipe)
                {
                    prevExtrude = lastExtrude;
                    lastExtrude = move;
                    lastPlane = layer.PlaneNormal;
                }

                if (move.Kind != MoveKind.Travel)
                {
                    newLayer.Moves.Add(move);
                    continue;
                }

                var hopStart = move.From;
                if (doWipe && lastExtrude is not null && !lastExtrude.IsLayerStitch
                    && !ShouldSkipWipeForTravel(move, layer, settings))
                {
                    var forced = ChooseWipeDirection(
                        move, lastExtrude, prevExtrude,
                        FindNextExtrude(toolpath, li, mi), lastPlane, settings);
                    var wipeMoves = BuildWipeMoves(move.From, lastExtrude, settings, forced).ToList();
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

        if (doEnd)
            AppendEndRetract(result, settings.EndRetractDistanceMm);

        return result;
    }

    /// <summary>
    /// One travel straight up in world Z from whatever point the print finished on.
    /// The lift is not along the last bead or the layer normal.
    /// </summary>
    private static void AppendEndRetract(Toolpath result, float distance)
    {
        for (int i = result.Layers.Count - 1; i >= 0; i--)
        {
            var layer = result.Layers[i];
            if (layer.Moves.Count == 0) continue;
            var last = layer.Moves[^1];
            var up = last.To + new Vector3(0f, 0f, distance);
            layer.Moves.Add(new ToolpathMove(last.To, up, MoveKind.Travel)
            {
                IsZHop = true,
                Normal = last.Normal,
            });
            return;
        }
    }

    /// <summary>
    /// When <see cref="SliceSettings.WipeSkipShortTravels"/> is on, skip wipe for
    /// travels shorter than twice the layer height (tiny jumps between beads).
    /// </summary>
    private static bool ShouldSkipWipeForTravel(
        ToolpathMove travel, ToolpathLayer layer, SliceSettings settings)
    {
        if (!settings.WipeSkipShortTravels) return false;
        float layerH = layer.Height > 0.05f ? layer.Height : MathF.Max(settings.LayerHeight, 0.1f);
        float threshold = 2f * layerH;
        float len = Vector3.Distance(travel.From, travel.To);
        return len < threshold;
    }

    private static IEnumerable<ToolpathMove> BuildWipeMoves(
        Vector3 travelStart, ToolpathMove lastExtrude, SliceSettings settings, Vector3? direction)
    {
        Vector3 dir;
        if (direction is { } given && given.LengthSquared() > 1e-8f)
            dir = Vector3.Normalize(given);
        else
        {
            var delta = lastExtrude.To - lastExtrude.From;
            if (delta.LengthSquared() < 1e-6f) yield break;

            dir = Vector3.Normalize(delta);
            if (settings.WipeMode == WipeMode.Retrace)
                dir = -dir;
        }

        float total = settings.WipeLengthMm;
        float ramp  = settings.WipeRampMm;
        var pos  = travelStart;
        var norm = lastExtrude.Normal;

        // Negative ramp: dip −Z into the bead (no RPM), then the full wipe length.
        // Shop −1 mm smash + reverse on this first wipe LIN cleans the nozzle.
        // The dip stops at the bed (Z = 0) instead of driving through it.
        if (ramp < 0f)
        {
            float smash = AllowedSmash(pos, settings);
            if (smash > 0.01f)
            {
                var down = pos + new Vector3(0f, 0f, -smash);
                yield return new ToolpathMove(pos, down, MoveKind.Extrude)
                {
                    IsWipe       = true,
                    WipeRpmScale = 0f,
                    Normal       = norm,
                };
                pos = down;
            }
            if (total > 0.01f)
            {
                var next = pos + dir * total;
                yield return new ToolpathMove(pos, next, MoveKind.Extrude)
                {
                    IsWipe       = true,
                    WipeRpmScale = 1f,
                    Normal       = norm,
                };
            }
            yield break;
        }

        float rampLen = Math.Min(ramp, total);
        float fullLen = total - rampLen;

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

    /// <summary>
    /// Preferred wipe heading for the mode, replaced when that heading would
    /// finish below <see cref="SliceSettings.BedZ"/>. Order is the mode's own
    /// direction, its opposite, then the two 90° sides (upward side first).
    /// If every heading still descends through the bed, the downward part is removed.
    /// </summary>
    private static Vector3? ChooseWipeDirection(
        ToolpathMove travel,
        ToolpathMove last,
        ToolpathMove? previous,
        ToolpathMove? next,
        Vector3 planeNormal,
        SliceSettings settings)
    {
        if (!TryBeadAxes(last, planeNormal, out var forward, out var side))
            return null;

        var back = -forward;
        Vector3 preferred = settings.WipeMode switch
        {
            WipeMode.Retrace => back,
            WipeMode.Perpendicular => PerpendicularWipeDirection(travel, last, previous, next, planeNormal),
            _ => forward,
        };

        var upwardSide = side.Z >= 0f ? side : -side;
        var downwardSide = -upwardSide;
        Vector3[] candidates = settings.WipeMode switch
        {
            WipeMode.Retrace => [back, forward, upwardSide, downwardSide],
            WipeMode.Perpendicular => [preferred, -preferred, forward, back],
            _ => [forward, back, upwardSide, downwardSide],
        };

        float smash = AllowedSmash(travel.From, settings);
        var origin = travel.From + new Vector3(0f, 0f, -smash);
        float length = MathF.Max(0f, settings.WipeLengthMm);
        float bed = settings.BedZ;

        foreach (var candidate in candidates)
        {
            if (candidate.LengthSquared() < 1e-8f) continue;
            var dir = Vector3.Normalize(candidate);
            if (origin.Z + dir.Z * length >= bed - 1e-3f)
                return dir;
        }

        var level = preferred.Z < 0f ? new Vector3(preferred.X, preferred.Y, 0f) : preferred;
        if (level.LengthSquared() < 1e-8f)
            level = new Vector3(forward.X, forward.Y, 0f);
        if (level.LengthSquared() < 1e-8f)
            level = Vector3.UnitX;
        return Vector3.Normalize(level);
    }

    /// <summary>
    /// Smash depth. Capped at the layer height, and also so the dip cannot pass the bed.
    /// </summary>
    private static float AllowedSmash(Vector3 pos, SliceSettings settings)
    {
        float ramp = settings.WipeRampMm;
        if (ramp >= 0f) return 0f;
        float smash = -ramp;
        float layerH = settings.LayerHeight > 0.05f ? settings.LayerHeight : 2f;
        if (smash > layerH)
            smash = layerH;
        float room = pos.Z - settings.BedZ;
        if (smash > room)
            smash = MathF.Max(0f, room);
        return smash < 0.01f ? 0f : smash;
    }

    private static bool TryBeadAxes(ToolpathMove last, Vector3 planeNormal, out Vector3 forward, out Vector3 side)
    {
        var delta = last.To - last.From;
        if (delta.LengthSquared() < 1e-8f)
        {
            forward = default;
            side = default;
            return false;
        }

        forward = Vector3.Normalize(delta);
        var n = planeNormal.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(planeNormal);
        var tangent = delta - n * Vector3.Dot(delta, n);
        if (tangent.LengthSquared() < 1e-8f)
            tangent = delta;
        tangent = Vector3.Normalize(tangent);
        side = Vector3.Cross(n, tangent);
        if (side.LengthSquared() < 1e-8f)
        {
            var axis = MathF.Abs(tangent.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            side = Vector3.Cross(tangent, axis);
        }
        side = side.LengthSquared() < 1e-8f ? Vector3.Zero : Vector3.Normalize(side);
        return true;
    }

    /// <summary>
    /// 90° in the bead's layer plane. The side is the one toward the closer of
    /// the previous bead and the next bead. A neighbor that sits on the bead
    /// axis does not count. With no sideways neighbor, lean toward the travel.
    /// </summary>
    private static Vector3 PerpendicularWipeDirection(
        ToolpathMove travel,
        ToolpathMove last,
        ToolpathMove? previous,
        ToolpathMove? next,
        Vector3 planeNormal)
    {
        if (!TryBeadAxes(last, planeNormal, out _, out var side) || side.LengthSquared() < 1e-8f)
            return Vector3.UnitX;

        const float tieMm = 0.5f;
        float bestDist = float.MaxValue;
        float bestLat = 0f;
        bool bestIsNew = false;
        bool any = false;

        void Consider(ToolpathMove? seg, bool isNew)
        {
            if (seg is null) return;
            var point = ClosestPointOnSegment(travel.From, seg.From, seg.To);
            float dist = Vector3.Distance(travel.From, point);
            float lateral = Vector3.Dot(point - travel.From, side);
            bool closer = dist < bestDist - tieMm;
            bool tiePreferNew = any && isNew && !bestIsNew && MathF.Abs(dist - bestDist) <= tieMm;
            if (!any || closer || tiePreferNew)
            {
                bestDist = dist;
                bestLat = lateral;
                bestIsNew = isNew;
                any = true;
            }
        }

        Consider(previous, false);
        Consider(next, true);

        if (!any || MathF.Abs(bestLat) < tieMm)
        {
            var n = planeNormal.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(planeNormal);
            var toward = travel.To - travel.From;
            toward -= n * Vector3.Dot(toward, n);
            float travelLat = Vector3.Dot(toward, side);
            if (MathF.Abs(travelLat) >= tieMm || !any)
                bestLat = travelLat;
        }

        return bestLat >= 0f ? side : -side;
    }

    private static ToolpathMove? FindNextExtrude(Toolpath toolpath, int layerIndex, int moveIndex)
    {
        var layer = toolpath.Layers[layerIndex];
        for (int i = moveIndex + 1; i < layer.Moves.Count; i++)
        {
            var move = layer.Moves[i];
            if (move.Kind == MoveKind.Extrude && !move.IsLayerStitch && !move.IsWipe)
                return move;
        }

        for (int li = layerIndex + 1; li < toolpath.Layers.Count; li++)
        {
            foreach (var move in toolpath.Layers[li].Moves)
            {
                if (move.Kind == MoveKind.Extrude && !move.IsLayerStitch && !move.IsWipe)
                    return move;
            }
        }

        return null;
    }

    private static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-8f) return a;
        float t = Math.Clamp(Vector3.Dot(point - a, ab) / len2, 0f, 1f);
        return a + ab * t;
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