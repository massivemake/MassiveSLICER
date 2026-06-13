using System.Text.RegularExpressions;
using MassiveSlicer.Core.C3Bridge;

namespace MassiveSlicer.Core.IO;

/// <summary>A single TOOL_DATA[] entry parsed from $config.dat.</summary>
public sealed record KrcToolEntry
{
    public required int   Index { get; init; }
    public required float X     { get; init; }
    public required float Y     { get; init; }
    public required float Z     { get; init; }
    public required float A     { get; init; }
    public required float B     { get; init; }
    public required float C     { get; init; }
}

/// <summary>A single BASE_DATA[] entry parsed from $config.dat.</summary>
public sealed record KrcBaseEntry
{
    public required int   Index { get; init; }
    public required float X     { get; init; }
    public required float Y     { get; init; }
    public required float Z     { get; init; }
    public required float A     { get; init; }
    public required float B     { get; init; }
    public required float C     { get; init; }
}

/// <summary>All TOOL_DATA and BASE_DATA entries read from a KRC4 $config.dat file.</summary>
public sealed record KrcDatSnapshot
{
    public IReadOnlyList<KrcToolEntry> Tools { get; init; } = [];
    public IReadOnlyList<KrcBaseEntry> Bases { get; init; } = [];
}

/// <summary>
/// Reads TOOL_DATA and BASE_DATA entries from a KUKA KRC4 $config.dat file
/// accessed via the standard KRC network share (\\{ip}\krc\...).
/// </summary>
public static class KrcDatReader
{
    private static readonly Regex ToolRx = new(
        @"TOOL_DATA\[(\d+)\]\s*=\s*\{([^}]+)\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BaseRx = new(
        @"BASE_DATA\[(\d+)\]\s*=\s*\{([^}]+)\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Reads $config.dat from the KRC4 network share at the given IP.
    /// UNC path: \\{ip}\krc\ROBOTER\KRC\R1\System\$config.dat
    /// </summary>
    public static async Task<KrcDatSnapshot> ReadAsync(string robotIp, CancellationToken ct = default)
    {
        var path = $@"\\{robotIp}\krc\ROBOTER\KRC\R1\System\$config.dat";
        var content = await File.ReadAllTextAsync(path, System.Text.Encoding.Latin1, ct);
        return Parse(content);
    }

    /// <summary>Parses TOOL_DATA and BASE_DATA entries from raw $config.dat text.</summary>
    public static KrcDatSnapshot Parse(string content)
    {
        var tools = new List<KrcToolEntry>();
        foreach (Match m in ToolRx.Matches(content))
        {
            if (!int.TryParse(m.Groups[1].Value, out int idx)) continue;
            var v = KrlVarParser.Parse(m.Groups[2].Value, ["X", "Y", "Z", "A", "B", "C"]);
            tools.Add(new KrcToolEntry
            {
                Index = idx,
                X = (float)v["X"], Y = (float)v["Y"], Z = (float)v["Z"],
                A = (float)v["A"], B = (float)v["B"], C = (float)v["C"],
            });
        }

        var bases = new List<KrcBaseEntry>();
        foreach (Match m in BaseRx.Matches(content))
        {
            if (!int.TryParse(m.Groups[1].Value, out int idx)) continue;
            var v = KrlVarParser.Parse(m.Groups[2].Value, ["X", "Y", "Z", "A", "B", "C"]);
            bases.Add(new KrcBaseEntry
            {
                Index = idx,
                X = (float)v["X"], Y = (float)v["Y"], Z = (float)v["Z"],
                A = (float)v["A"], B = (float)v["B"], C = (float)v["C"],
            });
        }

        return new KrcDatSnapshot
        {
            Tools = tools.OrderBy(t => t.Index).ToList(),
            Bases = bases.OrderBy(b => b.Index).ToList(),
        };
    }
}
