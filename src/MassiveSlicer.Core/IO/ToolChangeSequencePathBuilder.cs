using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>Builds densified playback paths from parsed KRL tool-change sequences.</summary>
public static class ToolChangeSequencePathBuilder
{
    const float DensifyStepMm = 25f;

    public static ToolChangeSequencePath? Build(
        ToolChangeSequence sequence,
        Func<float[], Vector3>? jointToRobroot = null)
    {
        var resolved = ResolveWaypoints(sequence.Waypoints, jointToRobroot);
        if (resolved.Count < 2) return null;

        var resolvedWaypoints = resolved
            .Select(r => new ResolvedToolChangeWaypoint(r.Position, r.Wp, r.Index))
            .ToList();

        var dense = new List<Vector3> { resolved[0].Position };
        var cum   = new List<float> { 0f };
        var segMove = new List<KrlMoveKind>();
        var wpAt = new List<int> { 0 };

        for (int i = 1; i < resolved.Count; i++)
        {
            var a = resolved[i - 1].Position;
            var b = resolved[i].Position;
            var dist = Vector3.Distance(a, b);
            int steps = Math.Max(1, (int)Math.Round(dist / DensifyStepMm));
            for (int s = 1; s <= steps; s++)
            {
                var t = s / (float)steps;
                dense.Add(Vector3.Lerp(a, b, t));
                cum.Add(cum[^1] + dist / steps);
                segMove.Add(resolved[i].Move);
            }
            wpAt.Add(dense.Count - 1);
        }

        var total = cum[^1];
        var toolEvent = DetectToolEvent(sequence, resolved, cum, wpAt, total);

        return new ToolChangeSequencePath
        {
            DensePoints          = dense,
            CumulativeLength     = cum,
            SegmentMoves         = segMove,
            WaypointAtDenseIndex = wpAt,
            TotalLength          = total,
            ToolEvent            = toolEvent,
            ResolvedWaypoints    = resolvedWaypoints,
        };
    }

    static List<(Vector3 Position, KrlMoveKind Move, ToolChangeWaypoint Wp, int Index)> ResolveWaypoints(
        IReadOnlyList<ToolChangeWaypoint> waypoints,
        Func<float[], Vector3>? jointToRobroot)
    {
        var outList = new List<(Vector3 Position, KrlMoveKind Move, ToolChangeWaypoint Wp, int Index)>();
        for (int i = 0; i < waypoints.Count; i++)
        {
            var wp = waypoints[i];
            Vector3? v = null;
            if (wp.Kind == "cart")
                v = new Vector3(wp.X, wp.Y, wp.Z);
            else if (wp.Kind == "joint" && wp.Joint is { } j && jointToRobroot is not null)
                v = jointToRobroot(j);

            if (v is null) continue;
            outList.Add((v.Value, wp.Move, wp, i));
        }
        return outList;
    }

    static ToolChangeToolEvent? DetectToolEvent(
        ToolChangeSequence sequence,
        List<(Vector3 Position, KrlMoveKind Move, ToolChangeWaypoint Wp, int Index)> pts,
        List<float> cum,
        List<int> wpAt,
        float total)
    {
        if (total <= 0f) return null;
        bool isPick = sequence.Definition.Id.Contains("Pick", StringComparison.OrdinalIgnoreCase);
        int evIdx = -1;
        for (int i = 0; i < pts.Count; i++)
        {
            var tt = pts[i].Wp.ToolType;
            if (isPick && !string.IsNullOrEmpty(tt) && tt != "0")
            {
                evIdx = i;
                break;
            }
            if (!isPick && tt == "0")
            {
                evIdx = i;
                break;
            }
        }

        if (evIdx < 0) return null;
        return new(cum[wpAt[evIdx]] / total, sequence.Definition.CellToolName, isPick);
    }
}