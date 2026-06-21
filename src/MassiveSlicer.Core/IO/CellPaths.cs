namespace MassiveSlicer.Core.IO;

/// <summary>
/// Resolves the canonical on-disk cell JSON directory and mirrors writes so dev-mode
/// edits survive publish/deploy copies under <c>%LOCALAPPDATA%\MassiveSlicer\build</c>.
/// </summary>
public static class CellPaths
{
    const string CellsMarker = "/assets/cells/";

    static readonly string?[] PreferredRoots =
    [
        Environment.GetEnvironmentVariable("MASSIVE_SLICER_CELLS"),
        @"\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER V2\assets\cells",
    ];

    /// <summary>
    /// Preferred <c>assets/cells</c> directory — repo / env override before publish copy.
    /// </summary>
    public static string? PreferredCellsDirectory()
    {
        foreach (var root in PreferredRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var full = Path.GetFullPath(root);
            if (Directory.Exists(full))
                return full;
        }

        return AssetPaths.FindCellsDirectory();
    }

    /// <summary>All cell JSON paths that should receive writes for <paramref name="cellPath"/>.</summary>
    public static IReadOnlyList<string> WriteTargetsFor(string cellPath)
    {
        var primary = Path.GetFullPath(cellPath);
        var rel     = RelativeUnderCells(primary);
        var targets = new List<string> { primary };

        if (rel is null) return targets;

        var preferred = PreferredCellsDirectory();
        if (preferred is not null)
        {
            var mirror = Path.GetFullPath(Path.Combine(preferred, rel));
            if (!PathsEqual(mirror, primary))
                targets.Add(mirror);
        }

        foreach (var root in AssetPaths.SearchRoots())
        {
            var mirror = Path.GetFullPath(Path.Combine(root, "assets", "cells", rel));
            if (!targets.Any(t => PathsEqual(t, mirror)))
                targets.Add(mirror);
        }

        return targets;
    }

    public static string? RelativeUnderCells(string fullPath)
    {
        var norm = fullPath.Replace('\\', '/');
        var idx  = norm.IndexOf(CellsMarker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        return norm[(idx + CellsMarker.Length)..].Replace('/', Path.DirectorySeparatorChar);
    }

    static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}