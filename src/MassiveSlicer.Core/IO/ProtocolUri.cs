namespace MassiveSlicer.Core.IO;

/// <summary>
/// <c>massiveslicer://open?path=</c> helper so a browser can prompt
/// "Open in MassiveSLICER" and hand us a .mass file.
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
