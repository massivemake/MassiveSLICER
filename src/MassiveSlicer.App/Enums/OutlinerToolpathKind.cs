using MassiveSlicer.Core.Models;

namespace MassiveSlicer.App.Enums;

/// <summary>
/// Distinguishes print vs mill toolpaths hanging off the same model in the outliner
/// so one does not overwrite the other.
/// </summary>
public enum OutlinerToolpathKind
{
    Print,
    Mill,
}

public static class OutlinerToolpathKinds
{
    public static OutlinerToolpathKind Infer(string? name, Toolpath? toolpath)
    {
        if (toolpath is not null)
        {
            foreach (var layer in toolpath.Layers)
            {
                foreach (var move in layer.Moves)
                {
                    if (move.Kind == MoveKind.Mill)
                        return OutlinerToolpathKind.Mill;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(name)
            && name.Contains("Mill", StringComparison.OrdinalIgnoreCase))
            return OutlinerToolpathKind.Mill;

        return OutlinerToolpathKind.Print;
    }
}
