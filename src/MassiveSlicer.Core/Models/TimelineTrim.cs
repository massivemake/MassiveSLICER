namespace MassiveSlicer.Core.Models;

/// <summary>
/// Copies a half-open scrub span of a toolpath without moving the points.
/// Scrub index 0 is the first move's From. Scrub index i (i &gt;= 1) is the end of
/// flat move i−1. Range [start, end) keeps moves whose flat index i satisfies
/// start &lt;= i &lt; end, so the approach target is the world point at scrub start
/// and the last LIN lands on scrub end.
/// </summary>
public static class TimelineTrim
{
    /// <summary>File-stem suffix for a section export, e.g. <c>_m100-200</c>.</summary>
    public static string ProgramSuffix(int startInclusive, int endExclusive)
        => $"_m{startInclusive}-{endExclusive}";

    /// <summary>
    /// Appends <see cref="ProgramSuffix"/> and keeps the result within
    /// <paramref name="maxLength"/> by shortening the stem, not the suffix.
    /// A stem that already ends with that suffix is left as one suffix.
    /// </summary>
    public static string AppendProgramSuffix(string? stem, int startInclusive, int endExclusive, int maxLength = 64)
    {
        var suffix = ProgramSuffix(startInclusive, endExclusive);
        stem ??= "";
        if (stem.EndsWith(suffix, StringComparison.Ordinal))
            stem = stem[..^suffix.Length].TrimEnd(' ', '-', '_');
        if (suffix.Length >= maxLength)
            return suffix[..maxLength];
        int room = maxLength - suffix.Length;
        if (stem.Length > room)
            stem = stem[..room].TrimEnd(' ', '-', '_');
        return stem + suffix;
    }

    /// <summary>
    /// A new toolpath whose kept moves are copies of the source span.
    /// Layer <see cref="ToolpathLayer.Index"/>, Z, height, and plane normal stay.
    /// Contours are not copied (their indices belong to the full move list).
    /// Throws <see cref="InvalidOperationException"/> when the span has no
    /// extrude or mill move, so the caller writes no empty program.
    /// </summary>
    public static Toolpath Extract(Toolpath source, int startInclusive, int endExclusive)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (startInclusive < 0)
            throw new ArgumentOutOfRangeException(nameof(startInclusive), "A trim index cannot be negative.");
        if (endExclusive < 0)
            throw new ArgumentOutOfRangeException(nameof(endExclusive), "A trim index cannot be negative.");
        if (endExclusive <= startInclusive)
            throw new InvalidOperationException(
                $"Section {startInclusive}–{endExclusive} is empty. Set the end after the start.");

        var result = new Toolpath { BeadWidthMm = source.BeadWidthMm };
        int flat = 0;
        bool anyCut = false;
        foreach (var layer in source.Layers)
        {
            ToolpathLayer? kept = null;
            foreach (var move in layer.Moves)
            {
                int index = flat++;
                if (index < startInclusive || index >= endExclusive)
                    continue;
                kept ??= new ToolpathLayer(layer.Index, layer.Z)
                {
                    Height = layer.Height,
                    PlaneNormal = layer.PlaneNormal,
                    ThermalTempC = layer.ThermalTempC,
                };
                var copy = move with { };
                kept.Moves.Add(copy);
                if (copy.Kind is MoveKind.Extrude or MoveKind.Mill)
                    anyCut = true;
            }
            if (kept is not null)
                result.Layers.Add(kept);
        }

        if (!anyCut)
            throw new InvalidOperationException(
                $"Section {startInclusive}–{endExclusive} has no print or mill move.");
        return result;
    }
}
