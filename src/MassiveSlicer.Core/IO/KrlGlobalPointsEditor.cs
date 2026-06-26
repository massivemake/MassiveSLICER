using System.Globalization;
using System.Text.RegularExpressions;

namespace MassiveSlicer.Core.IO;

/// <summary>Locates and updates named E6POS points in Global_Points.dat / sequence .dat files.</summary>
public static class KrlGlobalPointsEditor
{
    const string Num = @"(-?[\d.]+(?:E[+-]?\d+)?)";
    static readonly Regex E6PosRe = new(
        $@"(\w+)\s*=\s*\{{\s*X\s*{Num},\s*Y\s*{Num},\s*Z\s*{Num}," +
        $@"\s*A\s*{Num},\s*B\s*{Num},\s*C\s*{Num}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record PointPose(float X, float Y, float Z, float A, float B, float C);

    public sealed record PointLocation(
        string FilePath,
        int LineIndex,
        string Line,
        string[] Lines,
        PointPose Pose);

    public sealed record PointUpdateResult(
        bool Ok,
        bool Unchanged,
        string PointName,
        string FileName,
        string? BackupFileName,
        PointPose OldPose,
        PointPose NewPose);

    public static bool IsEditableNamedPoint(string name, string kind) =>
        kind == "cart"
        && !string.IsNullOrWhiteSpace(name)
        && !name.Equals("HOME", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("(inline)", StringComparison.Ordinal);

    public static PointLocation? LocatePoint(string pointName, string? krcRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(krcRoot)
            ? KrlToolChangeSequenceParser.ResolveKrcRoot()
            : krcRoot;
        if (root is null) return null;

        var programDir = Path.Combine(root, "Program");
        var files = new List<string> { Path.Combine(programDir, "Global_Points.dat") };
        files.AddRange(Directory.EnumerateFiles(programDir, "*.dat", SearchOption.TopDirectoryOnly));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!seen.Add(file) || !File.Exists(file)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith(';')) continue;
                var m = E6PosRe.Match(lines[i]);
                if (!m.Success || !m.Groups[1].Value.Equals(pointName, StringComparison.Ordinal))
                    continue;

                return new PointLocation(
                    file, i, lines[i], lines,
                    new PointPose(F(m, 2), F(m, 3), F(m, 4), F(m, 5), F(m, 6), F(m, 7)));
            }
        }

        return null;
    }

    public static PointUpdateResult UpdatePoint(string pointName, PointPose pose, string? krcRoot = null)
    {
        var loc = LocatePoint(pointName, krcRoot)
                  ?? throw new InvalidOperationException(
                      $"Point \"{pointName}\" not found in Global_Points.dat or program .dat files.");

        var newLine = RewritePoseInLine(loc.Line, pose);
        if (newLine == loc.Line)
        {
            return new PointUpdateResult(true, true, pointName, Path.GetFileName(loc.FilePath),
                null, loc.Pose, pose);
        }

        var backupDir = Path.Combine(Path.GetDirectoryName(loc.FilePath)!, ".slicer_backups");
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture);
        var backupName = $"{Path.GetFileName(loc.FilePath)}.{stamp}.bak";
        var backupPath = Path.Combine(backupDir, backupName);
        File.Copy(loc.FilePath, backupPath, overwrite: true);

        loc.Lines[loc.LineIndex] = newLine;
        File.WriteAllLines(loc.FilePath, loc.Lines);

        return new PointUpdateResult(
            true, false, pointName, Path.GetFileName(loc.FilePath), backupName, loc.Pose, pose);
    }

    static string RewritePoseInLine(string line, PointPose pose)
    {
        string outLine = line;
        foreach (var (key, val) in new[] {
            ("X", pose.X), ("Y", pose.Y), ("Z", pose.Z),
            ("A", pose.A), ("B", pose.B), ("C", pose.C) })
        {
            var re = new Regex($@"(\\b{key}\\s+){Num}", RegexOptions.IgnoreCase);
            if (!re.IsMatch(outLine))
                throw new InvalidOperationException($"Could not find {key} in point definition.");
            outLine = re.Replace(outLine, "${1}" + FmtNum(val), 1);
        }
        return outLine;
    }

    static string FmtNum(float v) =>
        (MathF.Round(v * 1_000_000f) / 1_000_000f).ToString("0.######", CultureInfo.InvariantCulture);

    static float F(Match m, int g) => float.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
}