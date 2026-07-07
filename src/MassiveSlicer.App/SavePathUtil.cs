using System.IO;

namespace MassiveSlicer.App;

/// <summary>Save-dialog path normalisation.</summary>
internal static class SavePathUtil
{
    /// <summary>
    /// Normalises a path returned by a save dialog so the file ALWAYS carries
    /// <paramref name="expectedExt"/>:
    ///   • collapses doubled extensions ("x.src.src" → "x.src") — some platforms append
    ///     the default extension to a suggested name that already carried one;
    ///   • replaces a wrong extension — the macOS save panel rewrites unregistered
    ///     extensions to another app's claimed preferred type (".src" → ".mod");
    ///   • appends the expected extension when missing entirely. Dots inside the
    ///     stem (e.g. "W5.5mm H3mm") are not mistaken for extensions.
    /// </summary>
    public static string Normalize(string path, string expectedExt)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (!expectedExt.StartsWith('.')) expectedExt = "." + expectedExt;

        var ext = Path.GetExtension(path);

        // Only treat it as a real extension when it looks like one (short, alphanumeric).
        bool plausible = ext.Length is > 1 and <= 6;
        if (plausible)
            foreach (var c in ext.AsSpan(1))
                if (!char.IsLetterOrDigit(c)) { plausible = false; break; }

        if (!plausible)
            return path + expectedExt;

        var stem = path[..^ext.Length];
        if (!ext.Equals(expectedExt, StringComparison.OrdinalIgnoreCase))
            return stem + expectedExt;                     // wrong extension → enforce

        while (stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            stem = stem[..^ext.Length];                    // collapse doubles
        return stem + ext;
    }
}
