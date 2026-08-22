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
    public const string PrintIcon  = "mdi-printer-3d-nozzle";
    public const string MillIcon   = "mdi-screw-lag";
    public const string PrintColor = "#37C871";
    public const string MillColor  = "#F27A3D";
    public const string PrintTip   = "Print toolpath";
    public const string MillTip    = "Mill toolpath";

    public static OutlinerToolpathKind Parse(string? kind)
        => string.Equals(kind, "Mill", StringComparison.OrdinalIgnoreCase)
            ? OutlinerToolpathKind.Mill
            : OutlinerToolpathKind.Print;

    public static string ToWorkspaceValue(OutlinerToolpathKind kind)
        => kind == OutlinerToolpathKind.Mill ? "Mill" : "Print";

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
