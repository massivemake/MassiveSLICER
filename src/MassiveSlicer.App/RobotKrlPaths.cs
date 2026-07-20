using System.Text;
using System.Text.RegularExpressions;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.App;

/// <summary>UNC paths and filenames for sending KRL programs to a cell controller.</summary>
internal static class RobotKrlPaths
{
    /// <summary>
    /// Max stem length (without <c>.src</c>). Leaves room for <c> Rev99</c> and keeps
    /// PointLoader / SMB paths comfortable.
    /// </summary>
    public const int MaxSrcStemLength = 64;

    /// <summary>Extended UNC path to the robot's D: drive share, e.g. <c>\\?\UNC\192.168.0.152\d</c>.</summary>
    public static string UncDFolder(CellConfig cell)
        => $@"\\?\UNC\{cell.BridgeIp.Trim()}\d";

    /// <summary>
    /// Default filename stem for export: <c>yyyy_MMdd - Name</c> (no extension).
    /// Preserves readable layout like <c>2026_0710 - Drone Print V90 Rev08</c> while
    /// stripping characters that break PointLoader / KUKA module open.
    /// </summary>
    public static string SuggestedFileName(string? baseName = null)
    {
        var stem = SanitizeStem(baseName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "PrintJob";

        // Names that already carry a date prefix (e.g. "2026_0706 - Floor Template V04")
        // keep that prefix; still sanitized for PointLoader-safe characters.
        if (Regex.IsMatch(stem, @"^\d{4}_\d{2,4}\b"))
            return stem;

        return SanitizeStem($"{DateTime.Now:yyyy_MMdd} - {stem}");
    }

    /// <summary>
    /// Full <c>.src</c> file name with optional revision: <c>… Rev08.src</c>.
    /// </summary>
    public static string SuggestedSrcFileName(string? baseName = null, int? rev = null)
    {
        var stem = SuggestedFileName(baseName);
        if (rev is { } r)
            stem = SanitizeStem($"{stem} Rev{r:00}");
        return stem + ".src";
    }

    /// <summary>
    /// Sanitizes a program / file stem for PointLoader while keeping human formatting:
    /// letters, digits, spaces, hyphens, underscores — e.g.
    /// <c>2026_0710 - Drone Print V90 Rev08</c>.
    /// </summary>
    public static string SanitizeStem(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var stem = Path.GetFileNameWithoutExtension(raw.Trim());

        // Normalize fancy punctuation to ASCII so layout stays readable.
        var normalized = new StringBuilder(stem.Length);
        foreach (var c in stem)
        {
            char mapped = c switch
            {
                '\u2013' or '\u2014' or '\u2212' or '\u2010' or '\u2011' => '-', // dashes
                '\u00A0' or '\u2007' or '\u202F' or '\t' or '\r' or '\n' => ' ',  // spaces
                '\u2018' or '\u2019' or '\u201C' or '\u201D' or '`' or '\'' or '"' => ' ',
                _ => c,
            };
            normalized.Append(mapped);
        }

        // Keep only PointLoader-friendly characters; drop the rest (parens, #, @, …).
        var kept = new StringBuilder(normalized.Length);
        foreach (var c in normalized.ToString())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_')
                kept.Append(c);
            else if (char.IsWhiteSpace(c))
                kept.Append(' ');
        }

        stem = kept.ToString();
        stem = Regex.Replace(stem, @"\s+", " ");
        stem = Regex.Replace(stem, @"-{2,}", "-");
        stem = Regex.Replace(stem, @"_{2,}", "_");
        stem = stem.Trim(' ', '-', '_', '.');

        if (stem.Length > MaxSrcStemLength)
            stem = stem[..MaxSrcStemLength].TrimEnd(' ', '-', '_', '.');

        return stem;
    }

    /// <summary>Normalizes a picked/saved path to extended UNC form when targeting a robot share.</summary>
    public static string ToExtendedUncPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return path;

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var unc = path.TrimStart('\\').Replace('/', '\\');
            return $@"\\?\UNC\{unc}";
        }

        return path;
    }
}
