namespace MassiveSlicer.Core.IO;

/// <summary>
/// Resolves <c>assets/...</c> paths whether the app runs from the repo root,
/// <c>bin/Release</c>, or a deployed <c>%LOCALAPPDATA%</c> folder.
/// </summary>
public static class AssetPaths
{
    /// <summary>Returns candidate content roots (exe dir, cwd, parents).</summary>
    public static IEnumerable<string> SearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateRootChain(Directory.GetCurrentDirectory()))
        {
            if (seen.Add(root))
                yield return root;
        }

        foreach (var root in EnumerateRootChain(AppContext.BaseDirectory))
        {
            if (seen.Add(root))
                yield return root;
        }
    }

    private static IEnumerable<string> EnumerateRootChain(string start)
    {
        var dir = Path.GetFullPath(start);
        for (int i = 0; i < 7 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return dir;
            dir = Path.GetDirectoryName(dir) ?? "";
        }
    }

    /// <summary>
    /// Resolves a relative asset path to an existing file, or returns the best-guess
    /// full path when missing (for error messages).
    /// </summary>
    public static string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;

        foreach (var candidate in CandidatePaths(relativePath))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
    }

    public static bool Exists(string relativePath)
        => File.Exists(Resolve(relativePath));

    /// <summary>First existing <c>assets/cells</c> directory, if any.</summary>
    public static string? FindCellsDirectory()
    {
        foreach (var root in SearchRoots())
        {
            var dir = Path.Combine(root, "assets", "cells");
            if (Directory.Exists(dir))
                return dir;
        }
        return null;
    }

    private const string ConnectLfam3Prefix =
        "reference/MassiveCONNECT-V2/MassiveCONNECT/webcells/LFAM3/";

    private static IEnumerable<string> CandidatePaths(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var variants   = new List<string> { normalized };

        // LFAM3 ships Toolheads/ on disk; JSON may say ToolHeads/.
        if (normalized.Contains("ToolHeads/", StringComparison.OrdinalIgnoreCase))
            variants.Add(normalized.Replace("ToolHeads/", "Toolheads/", StringComparison.OrdinalIgnoreCase));
        if (normalized.Contains("Toolheads/", StringComparison.OrdinalIgnoreCase))
            variants.Add(normalized.Replace("Toolheads/", "ToolHeads/", StringComparison.OrdinalIgnoreCase));

        // MassiveCONNECT webcells GLBs are meshopt-compressed; runtime decode prefers the smaller reference files.
        if (normalized.StartsWith(ConnectLfam3Prefix, StringComparison.OrdinalIgnoreCase))
        {
            var leaf = normalized[ConnectLfam3Prefix.Length..];
            variants.Add($"assets/cells/LFAM3/{leaf}");
        }

        foreach (var root in SearchRoots())
        {
            foreach (var variant in variants)
                yield return Path.GetFullPath(Path.Combine(root, variant));
        }
    }
}