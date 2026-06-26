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

    /// <summary>
    /// When true, a cell write is mirrored to every discovered source/repo copy of that cell
    /// (a dev convenience so edits from the published build propagate back to the repo source).
    /// <b>Off by default</b> so ordinary callers — the running app, scripts, and tests — only
    /// write the primary file and never reach into the repo. This fan-out was corrupting
    /// <c>lfam3.json</c>: any path containing <c>/assets/cells/</c> (e.g. a test's temp file)
    /// propagated to the hardcoded repo root and all source trees. Opt in for a dev sync by
    /// setting <c>MASSIVE_SLICER_MIRROR_CELLS=1</c>.
    /// </summary>
    public static bool MirrorToSourceTrees { get; set; }
        = string.Equals(Environment.GetEnvironmentVariable("MASSIVE_SLICER_MIRROR_CELLS"), "1", StringComparison.Ordinal);

    /// <summary>All cell JSON paths that should receive writes for <paramref name="cellPath"/>.</summary>
    public static IReadOnlyList<string> WriteTargetsFor(string cellPath)
    {
        var primary = Path.GetFullPath(cellPath);
        var rel     = RelativeUnderCells(primary);
        var targets = new List<string> { primary };

        // Default: write only the file we were given. Mirroring to repo/source copies is an
        // explicit dev opt-in — otherwise a temp or build-dir save silently overwrites the repo.
        if (rel is null || !MirrorToSourceTrees) return targets;

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