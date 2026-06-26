using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>Converts <see cref="Toolpath"/> to/from workspace JSON DTOs.</summary>
public static class ToolpathSerializer
{
    public static WorkspaceToolpathData ToData(Toolpath toolpath)
    {
        var data = new WorkspaceToolpathData();
        foreach (var layer in toolpath.Layers)
        {
            var layerDto = new WorkspaceToolpathLayerData
            {
                Index        = layer.Index,
                Z            = layer.Z,
                Height       = layer.Height,
                PlaneNormal  = ToArray(layer.PlaneNormal),
            };
            foreach (var move in layer.Moves)
            {
                layerDto.Moves.Add(new WorkspaceToolpathMoveData
                {
                    From          = ToArray(move.From),
                    To            = ToArray(move.To),
                    Kind          = move.Kind.ToString(),
                    Normal        = ToArray(move.Normal),
                    IsLayerChange = move.IsLayerChange,
                    IsLayerStitch = move.IsLayerStitch,
                    IsWipe           = move.IsWipe,
                    WipeRpmScale     = move.WipeRpmScale,
                    IsResumeRamp     = move.IsResumeRamp,
                    ResumeSpeedScale = move.ResumeSpeedScale,
                    ResumeRpmScale   = move.ResumeRpmScale,
                    IsZHop           = move.IsZHop,
                });
            }
            data.Layers.Add(layerDto);
        }
        return data;
    }

    public static Toolpath FromData(WorkspaceToolpathData data)
    {
        var toolpath = new Toolpath();
        foreach (var layerDto in data.Layers)
        {
            var normal = FromArray3(layerDto.PlaneNormal, Vector3.UnitZ);
            var layer  = new ToolpathLayer(layerDto.Index, layerDto.Z)
            {
                Height       = layerDto.Height,
                PlaneNormal  = normal,
            };
            foreach (var moveDto in layerDto.Moves)
            {
                var kind = Enum.TryParse<MoveKind>(moveDto.Kind, out var k) ? k : MoveKind.Extrude;
                layer.Moves.Add(new ToolpathMove(FromArray3(moveDto.From), FromArray3(moveDto.To), kind)
                {
                    Normal        = FromArray3(moveDto.Normal),
                    IsLayerChange = moveDto.IsLayerChange,
                    IsLayerStitch = moveDto.IsLayerStitch,
                    IsWipe           = moveDto.IsWipe,
                    WipeRpmScale     = moveDto.WipeRpmScale,
                    IsResumeRamp     = moveDto.IsResumeRamp,
                    ResumeSpeedScale = moveDto.ResumeSpeedScale,
                    ResumeRpmScale   = moveDto.ResumeRpmScale,
                    IsZHop           = moveDto.IsZHop,
                });
            }
            toolpath.Layers.Add(layer);
        }
        return toolpath;
    }

    private static float[] ToArray(Vector3 v) => [v.X, v.Y, v.Z];

    private static Vector3 FromArray3(float[] a, Vector3 fallback = default)
    {
        if (a.Length < 3) return fallback;
        return new Vector3(a[0], a[1], a[2]);
    }
}