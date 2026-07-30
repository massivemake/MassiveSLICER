namespace MassiveSlicer.Core.IO;

/// <summary>
/// Resolves the canonical on-disk cell JSON directory and mirrors writes so dev-mode
/// edits survive publish/deploy copies under <c>%LOCALAPPDATA%\MassiveSlicer\build</c>.
/// </summary>
public static class CellPaths
{
    const string CellsMarker = "/assets/cells/";

    /// <summary>Shop NAS copy of the cells tree. Opt-in only — see <see cref="PreferredRoots"/>.</summary>
    const string NasCellsRoot = @"\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER V2\assets\cells";

    /// <summary>
    /// Roots checked before the build's own <c>assets/cells</c>, in order.
    /// <para>
    /// <c>MASSIVE_SLICER_CELLS</c> — explicit override, always wins.
    /// </para>
    /// <para>
    /// <c>MASSIVE_SLICER_CELLS_NAS=1</c> — opt in to <see cref="NasCellsRoot"/>. This used to be
    /// preferred unconditionally, which meant any Windows machine that could reach the share
    /// silently loaded cell geometry from it in preference to whatever was committed — with no
    /// warning, and invisible unless you read the startup log. macOS cannot resolve the UNC path
    /// at all, so the same commit produced different bed positions on different machines. It cost
    /// a full day of debugging a "misplaced robot" that was really two diverging copies of
    /// lfam1.json. Opt-in keeps it available for anyone who genuinely wants the shared copy,
    /// without it being the silent default.
    /// </para>
    /// </summary>
    static string?[] PreferredRoots =>
    [
        Environment.GetEnvironmentVariable("MASSIVE_SLICER_CELLS"),
        string.Equals(Environment.GetEnvironmentVariable("MASSIVE_SLICER_CELLS_NAS"), "1", StringComparison.Ordinal)
            ? NasCellsRoot
            : null,
    ];

    /// <summary>
    /// Preferred <c>assets/cells</c> directory — env override, then the opt-in NAS share,
    /// then the build's own copy.
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
    /// True when the resolved cells directory is the shared NAS share rather than a local copy.
    /// Callers log this loudly: cell geometry coming off the network instead of the build is the
    /// single most confusing state this app can be in.
    /// </summary>
    public static bool IsNasCellsDirectory(string? directory)
        => !string.IsNullOrWhiteSpace(directory)
           && string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                            Path.GetFullPath(NasCellsRoot).TrimEnd(Path.DirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase);

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