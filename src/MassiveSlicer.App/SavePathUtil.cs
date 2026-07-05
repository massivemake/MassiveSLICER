using System.IO;

namespace MassiveSlicer.App;

/// <summary>Save-dialog path normalisation.</summary>
internal static class SavePathUtil
{
    /// <summary>
    /// Normalises a path returned by a save dialog:
    ///   • collapses doubled extensions ("x.src.src" → "x.src") — some platforms append
    ///     the default extension to a suggested name that already carried one;
    ///   • appends the expected extension when missing entirely.
    /// </summary>
    public static string Normalize(string path, string expectedExt)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (!expectedExt.StartsWith('.')) expectedExt = "." + expectedExt;

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
            return path + expectedExt;

        var stem = path[..^ext.Length];
        while (stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            stem = stem[..^ext.Length];
        return stem + ext;
    }
}
