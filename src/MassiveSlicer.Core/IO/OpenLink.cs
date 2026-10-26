namespace MassiveSlicer.Core.IO;

/// <summary>
/// What an "Open in MassiveSLICER" request asks for, after parsing and NAS mapping.
/// <list type="bullet">
/// <item><c>massiveslicer://open?path=Projects/…/part.mass</c> — open that project.</item>
/// <item><c>massiveslicer://open?path=Projects/…/part.stl&amp;saveTo=Projects/…/part.mass
/// &amp;project=42&amp;element=99</c> — start a project from the model; Save writes to
/// <see cref="SaveTo"/>. If SaveTo already exists it is opened instead (never re-imported).</item>
/// </list>
/// A double-clicked <c>.mass</c> (plain argv path) is an OpenLink with only <see cref="Path"/>.
/// </summary>
public sealed record OpenLink(string Path, string? SaveTo = null, string? Project = null, string? Element = null)
{
    /// <summary>Model files a link may start a project from (matches the Import menu).</summary>
    public static readonly IReadOnlySet<string> ModelExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".stl", ".obj", ".3mf", ".glb", ".gltf", ".step", ".stp" };

    public bool IsModel => IsModelPath(Path);

    public static bool IsMassPath(string p) => p.EndsWith(".mass", StringComparison.OrdinalIgnoreCase);

    public static bool IsModelPath(string p)
        => ModelExtensions.Contains(System.IO.Path.GetExtension(p))
           // A desktop-printer toolpath, not a mesh — it has its own importer.
           && !p.EndsWith(".gcode.3mf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a <c>massiveslicer:</c> link. Null when malformed: no path, a repeated
    /// parameter, a model without a <c>.mass</c> saveTo, or an unsupported file type.
    /// Paths are returned as written — call <see cref="ToLocal"/> to map them.
    /// </summary>
    public static OpenLink? Parse(string? raw)
    {
        if (!ProtocolUri.IsProtocol(raw?.Trim().Trim('"'))) return null;
        var s = raw!.Trim().Trim('"');
        int q = s.IndexOf('?');
        if (q < 0) return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in s[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            string key = kv[0].Equals("file", StringComparison.OrdinalIgnoreCase) ? "path" : kv[0];
            if (!KnownParams.Contains(key)) continue; // forward-compatible: ignore unknown params
            // Two paths in one link is ambiguous — refuse rather than pick one.
            if (!values.TryAdd(key, Uri.UnescapeDataString(kv[1].Replace('+', ' ')).Trim().Trim('"')))
                return null;
        }

        if (!values.TryGetValue("path", out var path) || path.Length == 0) return null;
        values.TryGetValue("saveTo", out var saveTo);
        values.TryGetValue("project", out var project);
        values.TryGetValue("element", out var element);
        saveTo = string.IsNullOrWhiteSpace(saveTo) ? null : saveTo;

        if (IsMassPath(path))
            return new OpenLink(path, null, Blank(project), Blank(element));
        if (IsModelPath(path) && saveTo is not null && IsMassPath(saveTo))
            return new OpenLink(path, saveTo, Blank(project), Blank(element));
        return null;
    }

    /// <summary>
    /// Maps <see cref="Path"/> and <see cref="SaveTo"/> onto this machine's MassiveFILES
    /// mount. Null if either is off the share — a link never reaches a local file.
    /// </summary>
    public OpenLink? ToLocal(IEnumerable<string> shareRoots, Func<string, bool> fileExists)
    {
        var roots = shareRoots.ToList();
        var path = UnasPaths.ToLocalPath(Path, roots, fileExists);
        if (path is null) return null;
        string? saveTo = null;
        if (SaveTo is not null)
        {
            saveTo = UnasPaths.ToLocalPath(SaveTo, roots, fileExists);
            if (saveTo is null) return null;
        }
        return this with { Path = path, SaveTo = saveTo };
    }

    /// <summary>
    /// First openable request in a process's argv: a link (mapped to the NAS) or a plain
    /// double-clicked <c>.mass</c> (left as-is).
    /// </summary>
    public static OpenLink? FromArgs(IEnumerable<string>? args, IEnumerable<string> shareRoots, Func<string, bool> fileExists)
    {
        if (args is null) return null;
        foreach (var raw in args)
        {
            if (ProtocolUri.IsProtocol(raw?.Trim().Trim('"')))
            {
                if (Parse(raw)?.ToLocal(shareRoots, fileExists) is { } link) return link;
                continue;
            }
            if (ProtocolUri.ResolveWorkspacePath(raw) is { Length: > 0 } mass)
                return new OpenLink(mass);
        }
        return null;
    }

    static readonly HashSet<string> KnownParams =
        new(StringComparer.OrdinalIgnoreCase) { "path", "saveTo", "project", "element" };

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
