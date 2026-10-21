using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Planar layers. Each mesh island keeps its outline. Infill lines fill the
/// closed area and stitch to the closest line. A bead is not repeated and does
/// not cross another bead on that layer. Islands are not joined. A lift
/// separates a line that cannot be stitched without a cross.
/// </summary>
public static class ContinuousPlanarSlicer
{
    public static Toolpath Slice(IReadOnlyList<Vector3[]> meshes, SliceSettings settings,
                                 Action<float>? progress = null)
    {
        var plain = new SliceSettings
        {
            LayerHeight = settings.LayerHeight,
            FirstLayerHeight = settings.FirstLayerHeight,
            BeadWidth = settings.BeadWidth,
            WallCount = 1,
            InfillPattern = InfillPattern.None,
            BrimEnabled = false,
            AdaptiveLayerHeight = settings.AdaptiveLayerHeight,
            AdaptiveQuality = settings.AdaptiveQuality,
            MinLayerHeight = settings.MinLayerHeight,
        };
        var toolpath = PlanarSlicer.Slice(meshes, plain, progress);
        float spacing = MathF.Max(settings.BeadWidth, 0.5f);
        Vector2? prevEnd = null;

        foreach (var layer in toolpath.Layers)
        {
            var loops = ClosedLoops(layer);
            if (loops.Count == 0) continue;
            var islands = ContinuousPlanarField.Build(loops, spacing, prevEnd);
            if (islands.Count == 0) continue;
            Replace(layer, islands, spacing);
            prevEnd = islands[^1][^1];
        }
        return toolpath;
    }

    static List<List<Vector2>> ClosedLoops(ToolpathLayer layer)
    {
        var loops = new List<List<Vector2>>();
        var chain = new List<Vector3>();
        void Flush()
        {
            if (chain.Count >= 3 && Vector3.Distance(chain[0], chain[^1]) <= 1.0f)
            {
                var loop = new List<Vector2>(chain.Count);
                foreach (var p in chain) loop.Add(new Vector2(p.X, p.Y));
                if (Vector2.Distance(loop[0], loop[^1]) < 1e-3f) loop.RemoveAt(loop.Count - 1);
                if (loop.Count >= 3) loops.Add(loop);
            }
            chain.Clear();
        }

        foreach (var move in layer.Moves)
        {
            // A layer stitch climbs in from the previous layer and is not part of this
            // closed section. Leaving it in the chain opens the mesh contour.
            if (move.Kind != MoveKind.Extrude || move.IsBrim
                || MathF.Abs(move.From.Z - move.To.Z) > 0.05f
                || MathF.Abs(move.To.Z - layer.Z) > 0.05f)
            {
                Flush();
                continue;
            }
            if (chain.Count == 0) chain.Add(move.From);
            else if (Vector3.Distance(chain[^1], move.From) > 1.0f)
            {
                Flush();
                chain.Add(move.From);
            }
            chain.Add(move.To);
        }
        Flush();
        return loops;
    }

    static void Replace(ToolpathLayer layer, List<List<Vector2>> islands, float clearance)
    {
        float z = layer.Z;
        float hop = z + MathF.Max(clearance, 1f);
        layer.Moves.Clear();
        layer.Contours.Clear();
        Vector3? last = null;
        foreach (var line in islands)
        {
            if (line.Count < 2) continue;
            var start = new Vector3(line[0].X, line[0].Y, z);
            if (last is { } prev && Vector3.DistanceSquared(prev, start) > 1e-4f)
            {
                // Leave the layer before crossing to the next closed region, so the
                // beads on this layer do not cut each other.
                var up = new Vector3(prev.X, prev.Y, hop);
                var over = new Vector3(start.X, start.Y, hop);
                layer.Moves.Add(new ToolpathMove(prev, up, MoveKind.Travel));
                layer.Moves.Add(new ToolpathMove(up, over, MoveKind.Travel));
                layer.Moves.Add(new ToolpathMove(over, start, MoveKind.Travel));
            }

            var prevPt = start;
            for (int i = 1; i < line.Count; i++)
            {
                var next = new Vector3(line[i].X, line[i].Y, z);
                if (Vector3.DistanceSquared(prevPt, next) < 1e-6f) continue;
                layer.Moves.Add(new ToolpathMove(prevPt, next, MoveKind.Extrude) { IsWall = true });
                prevPt = next;
            }
            last = prevPt;
        }
    }
}
