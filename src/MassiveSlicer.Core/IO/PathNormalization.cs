namespace MassiveSlicer.Core.IO;

/// <summary>Normalizes Windows extended-length and UNC paths for reliable file I/O.</summary>
public static class PathNormalization
{
    /// <summary>
    /// Strips <c>\\?\</c> / <c>\\?\UNC\</c> prefixes and returns a canonical full path when possible.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var trimmed = path.Trim().Trim('"');
        trimmed = StripExtendedPrefix(trimmed);

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return "\\" + path[8..];

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path[4..];

        return path;
    }
}