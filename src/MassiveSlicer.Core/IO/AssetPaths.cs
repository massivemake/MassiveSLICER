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

    /// <summary>First existing <c>assets/Images</c> directory (HDR backdrops), if any.</summary>
    public static string? FindImagesDirectory()
    {
        foreach (var root in SearchRoots())
        {
            var dir = Path.Combine(root, "assets", "Images");
            if (Directory.Exists(dir))
                return dir;
        }
        return null;
    }

    /// <summary>
    /// Canonical repo-relative path (<c>assets/...</c>) for a file under a search root,
    /// or <c>null</c> when the file is outside known asset trees.
    /// </summary>
    public static string? ToRelativeAssetPath(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        foreach (var root in SearchRoots())
        {
            var assetsRoot = Path.Combine(root, "assets");
            if (!full.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            return "assets" + full[assetsRoot.Length..].Replace('\\', '/');
        }
        return null;
    }

    /// <summary>All <c>assets/Images/*.hdr</c> backdrop files as canonical relative paths.</summary>
    public static IReadOnlyList<string> EnumerateBackdropHdrPaths()
    {
        var imagesDir = FindImagesDirectory();
        if (imagesDir is null)
            return [];

        return Directory.EnumerateFiles(imagesDir, "*.hdr", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => ToRelativeAssetPath(f) ?? f)
            .ToList();
    }

    /// <summary>True when two backdrop paths refer to the same HDR file.</summary>
    public static bool BackdropPathsEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return true;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            return string.Equals(
                Path.GetFullPath(Resolve(a)),
                Path.GetFullPath(Resolve(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private const string ConnectLfam3Prefix =
        "reference/MassiveCONNECT-V2/MassiveCONNECT/webcells/LFAM3/";

    private static IEnumerable<string> CandidatePaths(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var variants   = new List<string> { normalized };

        // Prefer the live repo / NAS cells tree before publish copies beside the .exe.
        if (normalized.StartsWith("assets/cells/", StringComparison.OrdinalIgnoreCase))
        {
            var preferred = CellPaths.PreferredCellsDirectory();
            if (preferred is not null)
            {
                var rel = normalized["assets/cells/".Length..];
                yield return Path.GetFullPath(Path.Combine(preferred, rel));
            }
        }

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