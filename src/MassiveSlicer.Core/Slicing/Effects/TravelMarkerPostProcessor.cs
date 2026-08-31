using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Splits print beads so Drive/SRC have a real vertex 100 mm before each hop
/// (<c>;Pre-Travel Start</c>) and 100 mm after it (<c>;Post-Travel Start</c>).
/// Hop = wipe run and/or travel (including z-hop). Geometry is unchanged.
/// </summary>
public static class TravelMarkerPostProcessor
{
    public const float MarkerDistanceMm = 100f;
    public const string PreTravelStartComment = ";Pre-Travel Start";
    public const string PostTravelStartComment = ";Post-Travel Start";
    /// <summary>Legacy name — same SRC comment as <see cref="PostTravelStartComment"/>.</summary>
    public const string PostTravelEndComment = PostTravelStartComment;

    public static Toolpath Apply(Toolpath toolpath, float distanceMm = MarkerDistanceMm)
    {
        if (toolpath.Layers.Count == 0) return toolpath;
        if (distanceMm < 0.05f) return toolpath;
        if (HasMarkers(toolpath)) return toolpath;
        if (!HasPrint(toolpath)) return toolpath;

        var flat = new List<(int Li, ToolpathMove Move)>();
        for (int li = 0; li < toolpath.Layers.Count; li++)
        {
            foreach (var m in toolpath.Layers[li].Moves)
                flat.Add((li, m));
        }

        if (flat.Count == 0) return toolpath;

        var hops = FindHops(flat);
        for (int h = hops.Count - 1; h >= 0; h--)
        {
            var (start, end) = hops[h];
            InsertPost(flat, end, distanceMm);
            InsertPre(flat, start, distanceMm);
        }

        return Rebuild(toolpath, flat);
    }

    public static bool HasMarkers(Toolpath toolpath)
    {
        foreach (var layer in toolpath.Layers)
        {
            foreach (var m in layer.Moves)
            {
                if (m.IsPreTravelStart || m.IsPostTravelEnd)
                    return true;
            }
        }
        return false;
    }

    private static bool HasPrint(Toolpath toolpath)
    {
        foreach (var layer in toolpath.Layers)
        {
            foreach (var m in layer.Moves)
            {
                if (IsPrint(m)) return true;
            }
        }
        return false;
    }

    private static List<(int Start, int End)> FindHops(List<(int Li, ToolpathMove Move)> flat)
    {
        var hops = new List<(int, int)>();
        int i = 0;
        while (i < flat.Count)
        {
            if (!IsHop(flat[i].Move))
            {
                i++;
                continue;
            }

            int start = i;
            i++;
            while (i < flat.Count && IsHop(flat[i].Move))
                i++;
            hops.Add((start, i));
        }
        return hops;
    }

    private static void InsertPre(List<(int Li, ToolpathMove Move)> flat, int hopStart, float distanceMm)
    {
        if (hopStart <= 0) return;
        int i = hopStart - 1;
        if (!IsPrint(flat[i].Move)) return;

        float need = distanceMm;
        while (i >= 0 && IsPrint(flat[i].Move) && need > 0.05f)
        {
            var m = flat[i].Move;
            float len = Vector3.Distance(m.From, m.To);
            if (len < 1e-4f)
            {
                i--;
                continue;
            }

            if (len + 1e-3f >= need)
            {
                var p = Vector3.Lerp(m.To, m.From, need / len);
                if (Vector3.DistanceSquared(p, m.From) <= 0.0025f)
                {
                    flat[i] = (flat[i].Li, m with { IsPreTravelStart = true });
                    return;
                }

                if (Vector3.DistanceSquared(p, m.To) <= 0.0025f)
                    return;

                flat[i] = (flat[i].Li, m with { To = p });
                flat.Insert(i + 1, (flat[i].Li, m with { From = p, IsPreTravelStart = true }));
                return;
            }

            need -= len;
            i--;
        }

        int first = Math.Max(0, i + 1);
        while (first < hopStart && !IsPrint(flat[first].Move))
            first++;
        if (first < hopStart && IsPrint(flat[first].Move))
            flat[first] = (flat[first].Li, flat[first].Move with { IsPreTravelStart = true });
    }

    private static void InsertPost(List<(int Li, ToolpathMove Move)> flat, int firstAfterHop, float distanceMm)
    {
        if (firstAfterHop >= flat.Count) return;
        int i = firstAfterHop;
        if (!IsPrint(flat[i].Move)) return;

        float need = distanceMm;
        while (i < flat.Count && IsPrint(flat[i].Move) && need > 0.05f)
        {
            var m = flat[i].Move;
            float len = Vector3.Distance(m.From, m.To);
            if (len < 1e-4f)
            {
                i++;
                continue;
            }

            if (len + 1e-3f >= need)
            {
                var p = Vector3.Lerp(m.From, m.To, need / len);
                if (Vector3.DistanceSquared(p, m.From) <= 0.0025f
                    || Vector3.DistanceSquared(p, m.To) <= 0.0025f)
                {
                    flat[i] = (flat[i].Li, m with { IsPostTravelEnd = true });
                    return;
                }

                flat[i] = (flat[i].Li, m with { To = p, IsPostTravelEnd = true });
                flat.Insert(i + 1, (flat[i].Li, m with { From = p }));
                return;
            }

            need -= len;
            i++;
        }

        int last = i - 1;
        if (last >= firstAfterHop && last < flat.Count && IsPrint(flat[last].Move))
            flat[last] = (flat[last].Li, flat[last].Move with { IsPostTravelEnd = true });
    }

    private static Toolpath Rebuild(Toolpath source, List<(int Li, ToolpathMove Move)> flat)
    {
        var result = new Toolpath { FormboundStats = source.FormboundStats };
        result.Warnings.AddRange(source.Warnings);

        int f = 0;
        for (int li = 0; li < source.Layers.Count; li++)
        {
            var src = source.Layers[li];
            var layer = new ToolpathLayer(src.Index, src.Z)
            {
                Height       = src.Height,
                PlaneNormal  = src.PlaneNormal,
                ThermalTempC = src.ThermalTempC,
            };
            layer.Contours.AddRange(src.Contours);
            while (f < flat.Count && flat[f].Li == li)
            {
                layer.Moves.Add(flat[f].Move);
                f++;
            }
            result.Layers.Add(layer);
        }

        return result;
    }

    private static bool IsHop(ToolpathMove m)
        => m.IsWipe || m.Kind == MoveKind.Travel;

    private static bool IsPrint(ToolpathMove m)
        => m.Kind == MoveKind.Extrude && !m.IsWipe;
}
