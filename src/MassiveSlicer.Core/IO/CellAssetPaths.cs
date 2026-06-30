using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>Collects on-disk model paths referenced by a <see cref="CellConfig"/>.</summary>
public static class CellAssetPaths
{
    /// <summary>Relative asset paths (e.g. <c>assets/cells/…/*.glb</c>) used by a cell.</summary>
    public static IEnumerable<string> AllModelPaths(CellConfig cell)
    {
        yield return cell.Robot.ModelPath;

        if (cell.Bed.ModelPath is { Length: > 0 } bed)
            yield return bed;

        if (cell.BoosterFrame is { } frame)
            yield return frame.ModelPath;

        if (cell.FlangeAttachment is { } flange)
            yield return flange.ModelPath;

        if (cell.RotaryBed is { } rotary)
        {
            yield return rotary.BottomPath;
            yield return rotary.TopPath;
        }

        foreach (var stand in cell.Stands)
            yield return stand.ModelPath;

        foreach (var tool in cell.EffectiveTools)
            yield return tool.ModelPath;
    }

    /// <summary>Resolved absolute paths that exist on disk (deduplicated).</summary>
    public static IEnumerable<string> ExistingResolvedPaths(CellConfig cell)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in AllModelPaths(cell))
        {
            var resolved = AssetPaths.Resolve(rel);
            if (!File.Exists(resolved)) continue;
            if (seen.Add(resolved))
                yield return resolved;
        }
    }

    /// <summary>Fingerprint of referenced asset mtimes/sizes — busts cell cache when meshes change.</summary>
    public static string AssetFingerprint(CellConfig cell)
    {
        var parts = new List<string>();
        foreach (var resolved in ExistingResolvedPaths(cell))
        {
            var fi = new FileInfo(resolved);
            parts.Add($"{resolved}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}");
        }

        parts.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join(";", parts);
    }
}