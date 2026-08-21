namespace MassiveSlicer.Core.IO;

/// <summary>
/// Converts a local mount / drive / UNC path into the UNAS share-relative
/// form MassiveLAB uses (<c>Projects/…</c>, <c>Research/…</c>).
/// </summary>
public static class UnasPaths
{
    public const string ShareName = "MassiveFILES";

    /// <summary>
    /// <c>/Volumes/MassiveFILES/Projects/a.mass</c>,
    /// <c>\\192.168.0.191\MassiveFILES\Projects\a.mass</c>,
    /// <c>Z:\Projects\a.mass</c> → <c>Projects/a.mass</c>.
    /// Null when the path is not on the share (a local Desktop file, etc.).
    /// </summary>
    public static string? ToShareRelative(string? path, string? unasProjectsRoot = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var n = path.Trim().Trim('"').Replace('\\', '/');
        if (n.StartsWith("//?/UNC/", StringComparison.OrdinalIgnoreCase))
            n = "//" + n["//?/UNC/".Length..];
        else if (n.StartsWith("//?/", StringComparison.Ordinal))
            n = n[4..];
        if (n.Length == 0) return null;

        var afterShare = AfterToken(n, "/" + ShareName + "/");
        if (afterShare is not null) return afterShare;

        if (!string.IsNullOrWhiteSpace(unasProjectsRoot))
        {
            var root = unasProjectsRoot.Trim().Trim('"').Replace('\\', '/').TrimEnd('/');
            if (root.Length > 0 && n.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var rel = n[root.Length..].Trim('/');
                var rootName = root.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Projects";
                return string.IsNullOrEmpty(rel) ? rootName : $"{rootName}/{rel}";
            }
        }

        // Shop PC mapped drive (Z:\Projects\…, Z:\Research\…) — only at the drive root
        // so "/Users/…/Projects/local.mass" is not treated as a share path.
        if (LooksLikeWindowsShareRoot(n))
        {
            foreach (var folder in new[] { "Projects", "Research" })
            {
                var hit = AfterToken(n, "/" + folder + "/");
                if (hit is not null) return folder + "/" + hit;
                if (n.EndsWith("/" + folder, StringComparison.OrdinalIgnoreCase))
                    return folder;
            }
        }

        // macOS /Volumes/<share>/rest when the share is not named MassiveFILES
        var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2 && parts[0].Equals("Volumes", StringComparison.OrdinalIgnoreCase))
            return string.Join('/', parts.Skip(2));

        return null;
    }

    static string? AfterToken(string normalized, string token)
    {
        int i = normalized.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = normalized[(i + token.Length)..].Trim('/');
        return rest.Length > 0 ? rest : null;
    }

    static bool LooksLikeWindowsShareRoot(string n)
        => n.Length >= 3
           && char.IsLetter(n[0])
           && n[1] == ':'
           && n[2] == '/';
}
