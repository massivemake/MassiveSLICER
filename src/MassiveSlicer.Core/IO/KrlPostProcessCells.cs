using System.Text.Json;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Per-robot KRL Post-Processing recipes inside one document. The document root is the
/// shared recipe plus a <see cref="KrlPostProcessSettings.Cells"/> map of complete
/// per-robot recipes keyed by cell name. Every operation here returns a new document and
/// never edits the shared part, so it stays exactly what builds from before per-robot
/// recipes read.
/// </summary>
/// <remarks>
/// Cell names match case-insensitively, the same way the rest of the app compares them.
/// </remarks>
public static class KrlPostProcessCells
{
    /// <summary>The robot's own recipe, or null when it has none yet.</summary>
    public static KrlPostProcessSettings? Find(KrlPostProcessSettings root, string? cellName)
    {
        if (string.IsNullOrWhiteSpace(cellName) || root.Cells is not { } cells) return null;
        foreach (var (name, recipe) in cells)
            if (string.Equals(name, cellName, StringComparison.OrdinalIgnoreCase))
                return recipe;
        return null;
    }

    /// <summary>
    /// The recipe a robot exports with: its own entry when it has one, else a copy of the
    /// shared recipe. The result never carries a <see cref="KrlPostProcessSettings.Cells"/> map.
    /// </summary>
    public static KrlPostProcessSettings Resolve(KrlPostProcessSettings root, string? cellName)
    {
        var own = Find(root, cellName);
        var copy = Clone(own ?? root);
        copy.Cells = null;
        return copy;
    }

    /// <summary>
    /// A copy of <paramref name="root"/> with <paramref name="cellName"/>'s entry replaced by
    /// <paramref name="recipe"/>. Other robots and the shared part are untouched.
    /// </summary>
    public static KrlPostProcessSettings WithCell(
        KrlPostProcessSettings root, string cellName, KrlPostProcessSettings recipe)
    {
        if (string.IsNullOrWhiteSpace(cellName))
            throw new ArgumentException("cell name is required", nameof(cellName));

        var result = Clone(root);
        var entry = Clone(recipe);
        entry.Cells = null;

        var cells = new Dictionary<string, KrlPostProcessSettings>();
        foreach (var (name, existing) in result.Cells ?? [])
            if (!string.Equals(name, cellName, StringComparison.OrdinalIgnoreCase))
                cells[name] = existing;
        cells[cellName] = entry;
        result.Cells = cells;
        return result;
    }

    /// <summary>
    /// A copy of <paramref name="lab"/> with each of <paramref name="saved"/>'s entries added
    /// back where the Lab has none. Entries the Lab already has always win.
    /// </summary>
    public static KrlPostProcessSettings RestoreMissing(
        KrlPostProcessSettings lab, IReadOnlyDictionary<string, KrlPostProcessSettings> saved,
        out IReadOnlyList<string> restored)
    {
        var result = Clone(lab);
        var added = new List<string>();
        foreach (var (name, recipe) in saved)
        {
            if (Find(result, name) is not null) continue;
            result = WithCell(result, name, recipe);
            added.Add(name);
        }
        added.Sort(StringComparer.OrdinalIgnoreCase);
        restored = added;
        return result;
    }

    /// <summary>Robot names with an entry, sorted.</summary>
    public static IReadOnlyList<string> Names(KrlPostProcessSettings root)
    {
        var names = (root.Cells?.Keys ?? Enumerable.Empty<string>()).ToList();
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>Deep copy through JSON, the same shape the file and the Lab store.</summary>
    public static KrlPostProcessSettings Clone(KrlPostProcessSettings s)
        => JsonSerializer.Deserialize<KrlPostProcessSettings>(
               JsonSerializer.Serialize(s, KrlPostProcessDocument.JsonOptions),
               KrlPostProcessDocument.JsonOptions)
           ?? new KrlPostProcessSettings();
}
