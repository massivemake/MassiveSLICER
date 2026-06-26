using System.Globalization;
using System.Text.RegularExpressions;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Parses LFAM3 KUKA tool-change programs (Extruder_Pick, Scanner_Deposit, …) into ordered
/// waypoints — mirrors MassiveCONNECT <c>krlSequenceParser.js</c>.
/// </summary>
public static class KrlToolChangeSequenceParser
{
    static readonly ToolChangeSequenceDef[] Catalog =
    [
        new("Extruder_Pick",    "Extruder · Pick",    "extruder", "HV Extruder"),
        new("Extruder_Deposit", "Extruder · Deposit", "extruder", "HV Extruder"),
        new("Spindle_Pick",     "Spindle · Pick",     "spindle",  "Spindle"),
        new("Spindle_Deposit",  "Spindle · Deposit",  "spindle",  "Spindle"),
        new("Scanner_Pick",     "Scanner · Pick",     "scanner",  "Scanner"),
        new("Scanner_Deposit",  "Scanner · Deposit",  "scanner",  "Scanner"),
    ];

    /// <summary>Per-cell override (from cell JSON <c>krcRoot</c>). Checked before built-in candidates.</summary>
    public static string? KrcRootOverride { get; set; }

    static readonly string[] KrcRootCandidates =
    [
        Environment.GetEnvironmentVariable("KRC_ROOT") ?? "",
        @"\\192.168.0.153\krc\ROBOTER\KRC\R1",
        @"\\192.168.0.191\MassiveFILES\Research\LFAM\Install\Kuka KRC 4\ROBOTER\KRC\R1",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "mnt", "kuka-lfam3-krc", "ROBOTER", "KRC", "R1"),
        @"Z:\ROBOTER\KRC\R1",
    ];

    const string Num = @"(-?[\d.]+(?:E[+-]?\d+)?)";
    static readonly Regex E6PosRe = new(
        $@"(\w+)\s*=\s*\{{\s*X\s*{Num},\s*Y\s*{Num},\s*Z\s*{Num}," +
        $@"\s*A\s*{Num},\s*B\s*{Num},\s*C\s*{Num}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex InlineRe = new(
        $@"\{{\s*(?:E6POS:)?\s*X\s*{Num},\s*Y\s*{Num},\s*Z\s*{Num}," +
        $@"\s*A\s*{Num},\s*B\s*{Num},\s*C\s*{Num}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex E6AxisRe = new(
        $@"XHOME\s*=\s*\{{\s*A1\s*{Num},\s*A2\s*{Num},\s*A3\s*{Num}," +
        $@"\s*A4\s*{Num},\s*A5\s*{Num},\s*A6\s*{Num}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex MoveRe = new(@"^\s*(SPTP|PTP|LIN|CIRC)\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex OutRe  = new(@"\$OUT\[(\d+)\]\s*=\s*(TRUE|FALSE)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex WaitForRe = new(@"WAIT\s+FOR\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex WaitSecRe = new(@"WAIT\s+SEC\s+([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ToolTypeRe = new(@"USRTOOLTYPE\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<ToolChangeSequenceDef> ListDefinitions()
    {
        var root = ResolveKrcRoot();
        var programDir = root is null ? null : Path.Combine(root, "Program");
        return Catalog.Select(s =>
        {
            bool available = programDir is not null
                             && File.Exists(Path.Combine(programDir, $"{s.Id}.src"));
            return s with { };
        }).ToList();
    }

    public static string? ResolveKrcRoot()
    {
        if (!string.IsNullOrWhiteSpace(KrcRootOverride)
            && Directory.Exists(Path.Combine(KrcRootOverride, "Program")))
            return KrcRootOverride;

        foreach (var c in KrcRootCandidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            if (Directory.Exists(Path.Combine(c, "Program")))
                return c;
        }
        return null;
    }

    public static bool IsSequenceAvailable(string id)
    {
        var root = ResolveKrcRoot();
        return root is not null
               && File.Exists(Path.Combine(root, "Program", $"{id}.src"));
    }

    public static ToolChangeSequence Parse(string id)
    {
        var def = Catalog.FirstOrDefault(s => s.Id == id)
                  ?? throw new InvalidOperationException($"Unknown sequence: {id}");

        var root = ResolveKrcRoot()
                   ?? throw new InvalidOperationException(
                       "KRC program folder not found. Set KRC_ROOT or install LFAM KRC files.");

        var programDir = Path.Combine(root, "Program");
        var srcPath = Path.Combine(programDir, $"{id}.src");
        if (!File.Exists(srcPath))
            throw new FileNotFoundException($"Sequence file not found: {id}.src", srcPath);

        var points = new Dictionary<string, (float X, float Y, float Z, float A, float B, float C)>(StringComparer.Ordinal);
        ParsePointFile(Path.Combine(programDir, "Global_Points.dat"), points);
        ParsePointFile(Path.Combine(programDir, $"{id}.dat"), points);
        var homeAxis = LoadHomeAxis(root);

        var pendingOutputs = new List<KrlOutputAnnotation>();
        var pendingWaits   = new List<KrlWaitAnnotation>();
        string? pendingToolType = null;
        var waypoints = new List<ToolChangeWaypoint>();

        void FlushInto(ToolChangeWaypoint wp)
        {
            waypoints.Add(wp with
            {
                Outputs = [.. pendingOutputs],
                Waits   = [.. pendingWaits],
                ToolType = pendingToolType ?? wp.ToolType,
            });
            pendingOutputs.Clear();
            pendingWaits.Clear();
            pendingToolType = null;
        }

        foreach (var raw in File.ReadAllLines(srcPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
                continue;

            var outM = OutRe.Match(line);
            if (outM.Success)
            {
                pendingOutputs.Add(new(int.Parse(outM.Groups[1].Value, CultureInfo.InvariantCulture),
                    outM.Groups[2].Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)));
                continue;
            }

            var wf = WaitForRe.Match(line);
            if (wf.Success)
            {
                pendingWaits.Add(new("signal", wf.Groups[1].Value.Trim().Replace("$IN", "IN", StringComparison.Ordinal), null));
                continue;
            }

            var ws = WaitSecRe.Match(line);
            if (ws.Success)
            {
                pendingWaits.Add(new("time", null, double.Parse(ws.Groups[1].Value, CultureInfo.InvariantCulture)));
                continue;
            }

            var tt = ToolTypeRe.Match(line);
            if (tt.Success)
            {
                pendingToolType = tt.Groups[1].Value;
                continue;
            }

            var mv = MoveRe.Match(line);
            if (!mv.Success) continue;

            var moveRaw = mv.Groups[1].Value.ToUpperInvariant();
            var move = moveRaw is "LIN" or "CIRC" ? KrlMoveKind.Lin : KrlMoveKind.Ptp;
            var target = mv.Groups[2].Value;

            ToolChangeWaypoint wp;
            if (target.Equals("XHOME", StringComparison.OrdinalIgnoreCase))
            {
                wp = new("HOME", "joint", 0, 0, 0, 0, 0, 0, homeAxis, move, null, [], []);
            }
            else if (target.StartsWith("{", StringComparison.Ordinal) || line.Contains('{', StringComparison.Ordinal))
            {
                var im = InlineRe.Match(line);
                if (!im.Success) continue;
                wp = new("(inline)", "cart",
                    F(im, 1), F(im, 2), F(im, 3), F(im, 4), F(im, 5), F(im, 6),
                    null, move, null, [], []);
            }
            else
            {
                var tok = target;
                if (!points.TryGetValue(tok, out var p))
                {
                    wp = new(tok, "unresolved", 0, 0, 0, 0, 0, 0, null, move, null, [], []);
                }
                else
                {
                    wp = new(tok, "cart", p.X, p.Y, p.Z, p.A, p.B, p.C, null, move, null, [], []);
                }
            }

            FlushInto(wp);
        }

        return new(def, waypoints);
    }

    static float F(Match m, int g) => float.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);

    static void ParsePointFile(string path,
        Dictionary<string, (float X, float Y, float Z, float A, float B, float C)> into)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.TrimStart().StartsWith(';')) continue;
            var m = E6PosRe.Match(line);
            if (!m.Success) continue;
            into[m.Groups[1].Value] = (F(m, 2), F(m, 3), F(m, 4), F(m, 5), F(m, 6), F(m, 7));
        }
    }

    static float[]? LoadHomeAxis(string root)
    {
        var configPath = Path.Combine(root, "System", "$config.dat");
        if (!File.Exists(configPath)) return null;
        var m = E6AxisRe.Match(File.ReadAllText(configPath));
        if (!m.Success) return null;
        return [F(m, 1), F(m, 2), F(m, 3), F(m, 4), F(m, 5), F(m, 6)];
    }
}