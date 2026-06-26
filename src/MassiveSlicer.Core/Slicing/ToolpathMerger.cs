using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>Concatenates toolpaths with z-hop retraction and travel connectors between them.</summary>
public static class ToolpathMerger
{
    public static Toolpath Merge(
        IReadOnlyList<Toolpath> toolpathsInOrder,
        float retractionHeightMm,
        float travelSpeedMps)
    {
        if (toolpathsInOrder.Count == 0)
            return new Toolpath();

        var moves = new List<ToolpathMove>();
        Vector3? lastPos = null;

        for (int ti = 0; ti < toolpathsInOrder.Count; ti++)
        {
            var tp = toolpathsInOrder[ti];
            foreach (var layer in tp.Layers)
            {
                foreach (var move in layer.Moves)
                {
                    moves.Add(CloneMove(move));
                    lastPos = move.To;
                }
            }

            if (ti < toolpathsInOrder.Count - 1)
            {
                var nextStart = FindFirstExtrudePoint(toolpathsInOrder[ti + 1]);
                if (lastPos is not null && nextStart is not null)
                    moves.AddRange(BuildMergeConnector(lastPos.Value, nextStart.Value, retractionHeightMm, travelSpeedMps));
            }
        }

        var layerZ = moves.FirstOrDefault(m => m.Kind == MoveKind.Extrude)?.From.Z
                     ?? moves.FirstOrDefault()?.From.Z
                     ?? 0f;
        var combined = new ToolpathLayer(0, layerZ) { PlaneNormal = Vector3.UnitZ };
        combined.Moves.AddRange(moves);

        var result = new Toolpath();
        result.Layers.Add(combined);
        return result;
    }

    public static Toolpath ToWorldSpace(Toolpath local, Vector3 origin, Matrix4x4 worldTransform)
    {
        var result = new Toolpath();
        foreach (var layer in local.Layers)
        {
            var newLayer = new ToolpathLayer(layer.Index, layer.Z)
            {
                Height      = layer.Height,
                PlaneNormal = TransformNormal(layer.PlaneNormal, worldTransform),
            };
            foreach (var move in layer.Moves)
            {
                newLayer.Moves.Add(move with
                {
                    From   = TransformPoint(move.From, origin, worldTransform),
                    To     = TransformPoint(move.To, origin, worldTransform),
                    Normal = TransformNormal(move.Normal, worldTransform),
                });
            }
            result.Layers.Add(newLayer);
        }
        return result;
    }

    private static IEnumerable<ToolpathMove> BuildMergeConnector(
        Vector3 endPos, Vector3 startPos, float zHop, float travelSpeedMps)
    {
        if (zHop <= 0f)
        {
            yield return new ToolpathMove(endPos, startPos, MoveKind.Travel)
            {
                IsMergeConnector = true,
                TravelSpeedMps   = travelSpeedMps,
            };
            yield break;
        }

        var liftEnd   = new Vector3(endPos.X, endPos.Y, endPos.Z + zHop);
        var liftStart = new Vector3(startPos.X, startPos.Y, startPos.Z + zHop);

        yield return new ToolpathMove(endPos, liftEnd, MoveKind.Travel)
        {
            IsZHop           = true,
            IsMergeConnector = true,
            TravelSpeedMps   = travelSpeedMps,
        };
        yield return new ToolpathMove(liftEnd, liftStart, MoveKind.Travel)
        {
            IsZHop           = true,
            IsMergeConnector = true,
            TravelSpeedMps   = travelSpeedMps,
        };
        yield return new ToolpathMove(liftStart, startPos, MoveKind.Travel)
        {
            IsZHop           = true,
            IsMergeConnector = true,
            TravelSpeedMps   = travelSpeedMps,
        };
    }

    private static Vector3? FindFirstExtrudePoint(Toolpath tp)
    {
        foreach (var layer in tp.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude)
                    return move.From;
            }
        }
        return null;
    }

    private static ToolpathMove CloneMove(ToolpathMove move) => move with { };

    private static Vector3 TransformPoint(Vector3 stored, Vector3 origin, Matrix4x4 wt)
        => Vector3.Transform(stored - origin, wt);

    private static Vector3 TransformNormal(Vector3 n, Matrix4x4 wt)
    {
        if (n.LengthSquared() < 1e-6f) return n;
        return Vector3.Normalize(Vector3.TransformNormal(n, wt));
    }
}