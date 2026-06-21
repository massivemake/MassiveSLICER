using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Splits the first extrusion moves after each travel into stepped speed/RPM ramp segments.
/// </summary>
public static class ResumeRampPostProcessor
{
    public static Toolpath Apply(Toolpath toolpath, SliceSettings settings)
    {
        if (!settings.ResumeRampEnabled
            || settings.ResumeRampDistanceMm <= 0f
            || settings.ResumeRampSteps < 1)
            return toolpath;

        float fullRpm = KrlAnout.ComputeRpmPercent(
            settings.BeadWidth, settings.LayerHeight, settings.PrintSpeedMps, settings.FlowRate);
        if (fullRpm < 1e-6f) return toolpath;

        float startSpeedScale = Math.Clamp(
            settings.ResumeRampStartSpeedMps / settings.PrintSpeedMps, 0f, 1f);
        float startRpmScale = Math.Clamp(
            settings.ResumeRampStartRpmPercent / fullRpm, 0f, 1f);

        if (startSpeedScale >= 0.999f && startRpmScale >= 0.999f)
            return toolpath;

        var result = new Toolpath();
        foreach (var layer in toolpath.Layers)
        {
            var pending = layer.Moves.ToList();
            var newLayer = new ToolpathLayer(layer.Index, layer.Z)
            {
                Height      = layer.Height,
                PlaneNormal = layer.PlaneNormal,
            };

            bool afterTravel = false;
            int i = 0;
            while (i < pending.Count)
            {
                var move = pending[i];
                if (move.Kind == MoveKind.Travel)
                {
                    newLayer.Moves.Add(move);
                    afterTravel = true;
                    i++;
                    continue;
                }

                if (afterTravel && IsRampableExtrude(move))
                {
                    var ramp = ConsumeRampSegments(
                        pending, ref i, settings, startSpeedScale, startRpmScale);
                    if (ramp.Count > 0)
                    {
                        newLayer.Moves.AddRange(ramp);
                        afterTravel = false;
                        continue;
                    }
                }

                newLayer.Moves.Add(move);
                afterTravel = false;
                i++;
            }

            result.Layers.Add(newLayer);
        }

        return result;
    }

    private static bool IsRampableExtrude(ToolpathMove move)
        => move.Kind == MoveKind.Extrude && !move.IsWipe && !move.IsLayerStitch;

    private static List<ToolpathMove> ConsumeRampSegments(
        List<ToolpathMove> moves,
        ref int index,
        SliceSettings settings,
        float startSpeedScale,
        float startRpmScale)
    {
        float segLen = settings.ResumeRampDistanceMm / settings.ResumeRampSteps;
        var output   = new List<ToolpathMove>(settings.ResumeRampSteps);

        for (int step = 0; step < settings.ResumeRampSteps; step++)
        {
            if (index >= moves.Count) break;
            var move = moves[index];
            if (!IsRampableExtrude(move)) break;

            var delta = move.To - move.From;
            float moveLen = delta.Length();
            if (moveLen < 1e-6f)
            {
                index++;
                step--;
                continue;
            }

            float take = Math.Min(segLen, moveLen);
            float t    = (step + 1) / (float)settings.ResumeRampSteps;
            float speedScale = startSpeedScale + (1f - startSpeedScale) * t;
            float rpmScale   = startRpmScale + (1f - startRpmScale) * t;

            var dir    = delta / moveLen;
            var segEnd = move.From + dir * take;

            output.Add(new ToolpathMove(move.From, segEnd, MoveKind.Extrude)
            {
                IsResumeRamp     = true,
                ResumeSpeedScale = speedScale,
                ResumeRpmScale   = rpmScale,
                Normal           = move.Normal,
            });

            if (take < moveLen - 0.01f)
                moves[index] = move with { From = segEnd };
            else
                index++;
        }

        return output;
    }
}