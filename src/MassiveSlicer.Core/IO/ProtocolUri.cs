namespace MassiveSlicer.Core.IO;

/// <summary>
/// <c>massiveslicer://open?path=</c> helper so a browser can prompt
/// "Open in MassiveSLICER" and hand us a .mass file. MassiveLAB sends the share-relative
/// path (<c>Projects/…/part.mass</c>); Windows <c>Z:\…</c> and macOS
/// <c>/Volumes/MassiveFILES/…</c> forms also work, on either OS.
/// </summary>
public static class ProtocolUri
{
    public const string Scheme = "massiveslicer";

    public static bool IsProtocol(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Trim().StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ResolveWorkspacePath(IEnumerable<string>? args)
    {
        if (args is null) return null;
        foreach (var raw in args)
        {
            var path = ResolveWorkspacePath(raw);
            if (!string.IsNullOrEmpty(path)) return path;
        }
        return null;
    }

    public static string? ResolveWorkspacePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim('"');
        if (s.Length == 0) return null;

        if (IsProtocol(s))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
            {
                // Some browsers drop the // and send massiveslicer:open?path=
                var q = s.IndexOf('?', StringComparison.Ordinal);
                if (q < 0) return null;
                return PathFromQuery(s[(q + 1)..]);
            }
            var fromQuery = PathFromQuery(uri.Query.TrimStart('?'));
            if (!string.IsNullOrEmpty(fromQuery)) return fromQuery;
            var rest = Uri.UnescapeDataString((uri.AbsolutePath ?? "").TrimStart('/'));
            return IsMassFile(rest) ? rest : null;
        }

        return IsMassFile(s) ? s : null;
    }

    /// <summary>
    /// Like <see cref="ResolveWorkspacePath(IEnumerable{string}?)"/>, but a
    /// <c>massiveslicer://</c> link is mapped onto this machine's MassiveFILES mount
    /// (<see cref="UnasPaths.ToLocalPath"/>) and refused if it is not on the share.
    /// A plain file argument (double-clicked .mass) is returned unchanged.
    /// </summary>
    public static string? ResolveLocalWorkspacePath(
        IEnumerable<string>? args, IEnumerable<string> shareRoots, Func<string, bool> fileExists)
    {
        if (args is null) return null;
        var roots = shareRoots.ToList();
        foreach (var raw in args)
        {
            var path = ResolveWorkspacePath(raw);
            if (string.IsNullOrEmpty(path)) continue;
            if (!IsProtocol(raw?.Trim().Trim('"'))) return path;
            var local = UnasPaths.ToLocalPath(path, roots, fileExists);
            if (local is not null) return local;
        }
        return null;
    }

    static string? PathFromQuery(string query)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (!kv[0].Equals("path", StringComparison.OrdinalIgnoreCase)
                && !kv[0].Equals("file", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = Uri.UnescapeDataString(kv[1].Replace('+', ' ')).Trim().Trim('"');
            return IsMassFile(path) ? path : null;
        }
        return null;
    }

    static bool IsMassFile(string path)
        => path.EndsWith(".mass", StringComparison.OrdinalIgnoreCase);
}
