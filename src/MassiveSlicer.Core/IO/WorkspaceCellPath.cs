namespace MassiveSlicer.Core.IO;

/// <summary>Resolves cell JSON paths stored in <c>.mass</c> workspace files.</summary>
public static class WorkspaceCellPath
{
    /// <summary>
    /// Stores a portable path when possible (<c>lfam2.json</c> or <c>assets/cells/…</c>),
    /// otherwise the absolute path.
    /// </summary>
    public static string? NormalizeForSave(string? cellPath)
    {
        if (string.IsNullOrWhiteSpace(cellPath)) return null;

        var full = Path.GetFullPath(cellPath);
        if (CellPaths.RelativeUnderCells(full) is { } rel)
            return rel.Replace('\\', '/');

        return full;
    }

    /// <summary>
    /// Resolves a saved workspace cell path against discovered cells and known asset roots.
    /// </summary>
    public static string? Resolve(string? savedPath, IReadOnlyList<string> discoveredCellPaths)
    {
        if (string.IsNullOrWhiteSpace(savedPath)) return null;

        var trimmed = savedPath.Trim().Replace('/', Path.DirectorySeparatorChar);
        string fileName = Path.GetFileName(trimmed);

        // Prefer the app's discovered install cells (beside the .exe) over a stale NAS/repo absolute path.
        foreach (var discovered in discoveredCellPaths)
        {
            if (string.Equals(Path.GetFileName(discovered), fileName, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(discovered);
        }

        string? relFromSaved = CellPaths.RelativeUnderCells(trimmed);
        if (relFromSaved is null && Path.IsPathRooted(trimmed))
            relFromSaved = CellPaths.RelativeUnderCells(Path.GetFullPath(trimmed));

        if (relFromSaved is { } rel)
        {
            foreach (var candidate in CandidatePathsForRelative(rel))
            {
                if (File.Exists(candidate)) return candidate;
            }
        }
        else if (!Path.IsPathRooted(trimmed))
        {
            foreach (var candidate in CandidatePathsForRelative(trimmed))
            {
                if (File.Exists(candidate)) return candidate;
            }
        }

        if (Path.IsPathRooted(trimmed))
        {
            var full = Path.GetFullPath(trimmed);
            if (File.Exists(full)) return full;
        }

        return null;
    }

    /// <summary>True when <paramref name="activeCellPath"/> is the same cell as the workspace saved path.</summary>
    public static bool Matches(string? savedPath, string? activeCellPath, IReadOnlyList<string> discoveredCellPaths)
    {
        if (string.IsNullOrWhiteSpace(savedPath)) return true;
        if (string.IsNullOrWhiteSpace(activeCellPath)) return false;

        var expected = Resolve(savedPath, discoveredCellPaths);
        if (expected is null)
        {
            return string.Equals(
                Path.GetFileName(savedPath),
                Path.GetFileName(activeCellPath),
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(Path.GetFullPath(expected), Path.GetFullPath(activeCellPath), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CandidatePathsForRelative(string relative)
    {
        relative = relative.Replace('/', Path.DirectorySeparatorChar);

        if (CellPaths.PreferredCellsDirectory() is { } preferred)
            yield return Path.GetFullPath(Path.Combine(preferred, relative));

        if (AssetPaths.FindCellsDirectory() is { } fallback)
            yield return Path.GetFullPath(Path.Combine(fallback, relative));

        foreach (var root in AssetPaths.SearchRoots())
            yield return Path.GetFullPath(Path.Combine(root, "assets", "cells", relative));
    }
}