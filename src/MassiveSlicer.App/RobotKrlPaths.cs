using System.Text.RegularExpressions;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.App;

/// <summary>UNC paths and filenames for sending KRL programs to a cell controller.</summary>
internal static class RobotKrlPaths
{
    /// <summary>Extended UNC path to the robot's D: drive share, e.g. <c>\\?\UNC\192.168.0.152\d</c>.</summary>
    public static string UncDFolder(CellConfig cell)
        => $@"\\?\UNC\{cell.BridgeIp.Trim()}\d";

    /// <summary>Default filename: <c>yyyy_MMdd - Name.src</c>.</summary>
    public static string SuggestedFileName(string? baseName = null)
    {
        var stem = SanitizeStem(baseName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "PrintJob";
        return $"{DateTime.Now:yyyy_MMdd} - {stem}.src";
    }

    /// <summary>Normalizes a picked/saved path to extended UNC form when targeting a robot share.</summary>
    public static string ToExtendedUncPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return path;

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var unc = path.TrimStart('\\').Replace('/', '\\');
            return $@"\\?\UNC\{unc}";
        }

        return path;
    }

    private static string SanitizeStem(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var stem = Path.GetFileNameWithoutExtension(raw.Trim());
        stem = Regex.Replace(stem, @"[<>:""/\\|?*]", "_");
        return stem.Trim();
    }
}