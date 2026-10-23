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

    /// <summary>
    /// The reverse of <see cref="ToShareRelative"/>: turns a link's path into this machine's
    /// path for the same NAS file, so one MassiveLAB link works on Windows and macOS.
    /// Accepts share-relative (<c>Projects/a.mass</c>) or any absolute share form
    /// (<c>Z:\…</c>, <c>/Volumes/MassiveFILES/…</c>, UNC). Returns the first candidate under
    /// <paramref name="shareRoots"/> that exists, else the first candidate (so the open fails
    /// with a real path in the message). Null when the path is not on the share or tries to
    /// climb out of it — links never open arbitrary local files.
    /// </summary>
    public static string? ToLocalPath(
        string? linkPath, IEnumerable<string> shareRoots, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(linkPath)) return null;
        var p = linkPath.Trim().Trim('"');

        bool absolute = p.StartsWith('/') || p.StartsWith('\\') || (p.Length >= 2 && p[1] == ':');
        var rel = absolute ? ToShareRelative(p) : p.Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(rel)) return null;

        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".." || s.Contains(':')))
            return null;

        string? first = null;
        foreach (var root in shareRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            char sep = root.Contains('\\') || (root.Length >= 2 && root[1] == ':') ? '\\' : '/';
            var candidate = root.TrimEnd('/', '\\') + sep + string.Join(sep, segments);
            first ??= candidate;
            if (fileExists(candidate)) return candidate;
        }
        return first;
    }

    /// <summary>
    /// Where the MassiveFILES share is mounted on this machine, most likely first.
    /// <paramref name="unasProjectsRoot"/> is the user's preference (its parent is the share).
    /// macOS mounts a second connection as <c>MassiveFILES-1</c>, so those count too.
    /// </summary>
    public static IReadOnlyList<string> LocalShareRoots(string? unasProjectsRoot)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(unasProjectsRoot))
        {
            var r = unasProjectsRoot.Trim().TrimEnd('/', '\\');
            var name = r.Split('/', '\\').LastOrDefault() ?? "";
            if (name is "Projects" or "Research")
                roots.Add(r[..^name.Length].TrimEnd('/', '\\') is { Length: > 0 } parent
                    ? (parent.EndsWith(':') ? parent + "\\" : parent)
                    : r);
        }

        if (OperatingSystem.IsWindows())
        {
            roots.Add(@"Z:\");
            roots.Add(@"\\192.168.0.191\" + ShareName);
        }
        else
        {
            roots.Add("/Volumes/" + ShareName);
            try
            {
                roots.AddRange(Directory.GetDirectories("/Volumes", ShareName + "-*").Order());
            }
            catch { /* no /Volumes (Linux CI) */ }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary><see cref="LocalShareRoots(string?)"/> using this machine's saved preference.</summary>
    public static IReadOnlyList<string> LocalShareRoots()
    {
        string? pref = null;
        try { pref = PreferencesLoader.Load().UnasProjectsRoot; }
        catch { /* unreadable prefs — fall back to the default mounts */ }
        return LocalShareRoots(pref);
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
