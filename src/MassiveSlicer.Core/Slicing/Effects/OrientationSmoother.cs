using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Post-processing pass that smooths per-move toolhead orientations (normals) across each
/// contour using a box-filter average and/or a bidirectional slew-rate limiter, preventing
/// sharp ABC reorientation jumps that would cause robot over-acceleration on tight curves
/// or steep overhang transitions.
///
/// Only the Normal field (orientation) is modified — XYZ positions are unchanged.
/// Each contour run (between travel/stitch moves) is smoothed independently so orientations
/// never bleed across air moves.
/// </summary>
public static class OrientationSmoother
{
    public static Toolpath Apply(Toolpath toolpath, SliceSettings settings)
    {
        bool doSmooth = settings.SmoothRotation;
        bool doRate   = settings.SmoothRotationMaxRateDegPerMm > 0f;
        if (!doSmooth && !doRate) return toolpath;

        int   radius  = Math.Max(1, settings.SmoothRotationRadius);
        float maxRate = settings.SmoothRotationMaxRateDegPerMm;
        var   result  = new Toolpath();

        foreach (var layer in toolpath.Layers)
        {
            var newLayer = new ToolpathLayer(layer.Index, layer.Z)
                { Height = layer.Height, PlaneNormal = layer.PlaneNormal };

            int i = 0;
            while (i < layer.Moves.Count)
            {
                var move = layer.Moves[i];
                if (move.Kind == MoveKind.Travel || move.IsLayerStitch || move.IsWipe || move.IsZHop)
                {
                    newLayer.Moves.Add(move);
                    i++;
                    continue;
                }

                int runStart = i;
                while (i < layer.Moves.Count &&
                       layer.Moves[i].Kind != MoveKind.Travel &&
                       !layer.Moves[i].IsLayerStitch)
                    i++;

                ProcessContourRun(layer.Moves, runStart, i, newLayer, radius, maxRate, doSmooth, doRate);
            }

            result.Layers.Add(newLayer);
        }

        return result;
    }

    private static void ProcessContourRun(
        List<ToolpathMove> moves, int start, int end,
        ToolpathLayer layer, int radius, float maxDegPerMm,
        bool doSmooth, bool doRate)
    {
        int count = end - start;

        bool hasNormals = false;
        for (int i = start; i < end && !hasNormals; i++)
            if (moves[i].Normal.LengthSquared() > 1e-6f) hasNormals = true;

        if (!hasNormals)
        {
            for (int i = start; i < end; i++) layer.Moves.Add(moves[i]);
            return;
        }

        var normals = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            var n = moves[start + i].Normal;
            normals[i] = n.LengthSquared() > 1e-6f ? n : Vector3.UnitZ;
        }

        if (doSmooth)
        {
            var smoothed = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                var sum = Vector3.Zero;
                int lo = Math.Max(0, i - radius);
                int hi = Math.Min(count - 1, i + radius);
                for (int j = lo; j <= hi; j++)
                    sum += normals[j];
                float len = sum.Length();
                smoothed[i] = len > 1e-6f ? sum / len : normals[i];
            }
            normals = smoothed;
        }

        if (doRate)
        {
            var positions = new Vector3[count];
            for (int i = 0; i < count; i++)
                positions[i] = moves[start + i].To;
            normals = RateLimitPass(normals, positions, maxDegPerMm);
        }

        for (int i = 0; i < count; i++)
            layer.Moves.Add(moves[start + i] with { Normal = normals[i] });
    }

    private static Vector3[] RateLimitPass(Vector3[] normals, Vector3[] positions, float maxDegPerMm)
    {
        float maxRadPerMm = maxDegPerMm * (MathF.PI / 180f);
        int   count       = normals.Length;

        var fwd = new Vector3[count];
        fwd[0] = normals[0];
        for (int i = 1; i < count; i++)
        {
            float dist     = (positions[i] - positions[i - 1]).Length();
            float maxAngle = maxRadPerMm * MathF.Max(dist, 0.1f);
            fwd[i] = SlerpClamp(fwd[i - 1], normals[i], maxAngle);
        }

        var bwd = new Vector3[count];
        bwd[count - 1] = normals[count - 1];
        for (int i = count - 2; i >= 0; i--)
        {
            float dist     = (positions[i] - positions[i + 1]).Length();
            float maxAngle = maxRadPerMm * MathF.Max(dist, 0.1f);
            bwd[i] = SlerpClamp(bwd[i + 1], normals[i], maxAngle);
        }

        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            var   avg = fwd[i] + bwd[i];
            float len = avg.Length();
            result[i] = len > 1e-6f ? avg / len : normals[i];
        }
        return result;
    }

    // Rotate 'from' toward 'to' but clamp the angle to maxAngle (radians).
    private static Vector3 SlerpClamp(Vector3 from, Vector3 to, float maxAngle)
    {
        float cosA  = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        float angle = MathF.Acos(cosA);
        if (angle <= maxAngle) return to;

        var   axis    = Vector3.Cross(from, to);
        float axisLen = axis.Length();
        if (axisLen < 1e-6f) return from;
        axis /= axisLen;

        // Rodrigues: from is perpendicular to axis so the dot term vanishes.
        return from * MathF.Cos(maxAngle) + Vector3.Cross(axis, from) * MathF.Sin(maxAngle);
    }
}
