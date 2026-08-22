using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// RobotCodeEditor 1.0.6 four-pass SRC rewrite: consecutive travel-start ignore,
/// short-travel ignore, insertion forms, then URM enter/exit along the bead.
/// Expects exporter to have written <c>;travel end</c> / <c>;travel start</c> pairs
/// (wipe opens a travel). Time offsets use the job print speed. Stop
/// <c>$VEL.CP</c> is half of that speed.
/// </summary>
public static class CodeEditorSrcInjector
{
    public const string ModifiedTag = "; !Modified by RCE!";
    public const string IgnoredPrefix = "; !Ignored! ";

    private static readonly Regex LinXyz = new(
        @"LIN\s*\{\s*X\s*([-\d.]+),\s*Y\s*([-\d.]+),\s*Z\s*([-\d.]+)",
        RegexOptions.Compiled);
    private static readonly Regex LinAbc = new(
        @"A\s*([-\d.]+),\s*B\s*([-\d.]+),\s*C\s*([-\d.]+)",
        RegexOptions.Compiled);
    private static readonly Regex LinExt = new(
        @"E1\s*([-\d.]+),\s*E2\s*([-\d.]+),\s*E3\s*([-\d.]+),\s*E4\s*([-\d.]+),\s*E5\s*([-\d.]+),\s*E6\s*([-\d.]+)",
        RegexOptions.Compiled);
    private static readonly Regex TriggerDo = new(
        @"^\s*TRIGGER\s+WHEN\s+DISTANCE\s*=\s*0\s+DELAY\s*=\s*0\s+DO\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Apply(string src, KrlExportSettings export)
    {
        var recipe = (export.CodeEditorInject ?? new CodeEditorInjectSettings()).Clone();
        double speed = export.PrintSpeedMps * 1000.0;
        if (speed < 1e-6)
            speed = 30.0;
        recipe.StopExtrudingCommand = CodeEditorInjectSettings.WithHalfPrintVel(
            recipe.StopExtrudingCommand, export.PrintSpeedMps);

        var lines = SplitKeep(src);
        IgnoreConsecutiveTravelStarts(lines);
        IgnoreShortTravels(lines, recipe.ShortTravelThresholdMm);
        InsertAfterTravelEnd(lines, recipe);
        InsertStopExtruding(lines, recipe, speed);
        InjectUrm(lines, recipe, speed);
        return string.Join("\r\n", lines) + (src.EndsWith('\n') || src.EndsWith('\r') ? "\r\n" : "");
    }

    private static List<string> SplitKeep(string src)
    {
        var lines = new List<string>();
        using var reader = new StringReader(src);
        string? line;
        while ((line = reader.ReadLine()) != null)
            lines.Add(line);
        return lines;
    }

    private static bool Starts(string line, string prefix)
        => line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnored(string line)
        => line.Contains(IgnoredPrefix, StringComparison.Ordinal);

    private static void IgnoreConsecutiveTravelStarts(List<string> lines)
    {
        bool pending = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (IsIgnored(lines[i]))
                continue;
            if (Starts(lines[i], ";travel start"))
            {
                if (pending)
                    lines[i] = TagIgnored(lines[i]);
                pending = true;
            }
            else if (Starts(lines[i], ";travel end"))
                pending = false;
        }
    }

    private static void IgnoreShortTravels(List<string> lines, double thresholdMm)
    {
        int startIdx = -1, endIdx = -1;
        Target? first = null;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (IsIgnored(lines[i]))
                continue;
            if (Starts(lines[i], ";travel end"))
            {
                startIdx = i;
                first = null;
                endIdx = -1;
            }
            else if (Starts(lines[i], ";travel start") && startIdx >= 0)
            {
                endIdx = i;
            }
            else if (TryParseLin(lines[i], out var t))
            {
                if (endIdx < 0 && startIdx >= 0 && first == null)
                    first = t;
                else if (endIdx >= 0 && first != null)
                {
                    double d = Dist(first.Value, t);
                    if (d < thresholdMm)
                    {
                        lines[startIdx] = TagIgnored(lines[startIdx]);
                        lines[endIdx] = TagIgnored(lines[endIdx]);
                    }
                    startIdx = endIdx = -1;
                    first = null;
                }
            }
        }
    }

    private static void InsertAfterTravelEnd(List<string> lines, CodeEditorInjectSettings r)
    {
        var cmd = PrepareCommand(r.StartExtrudingCommand, r.PointLoaderSafeIo);
        if (cmd.Count == 0)
            return;
        for (int i = 0; i < lines.Count; i++)
        {
            if (IsIgnored(lines[i]) || !Starts(lines[i], ";travel end"))
                continue;
            InsertBlock(lines, i + 1, cmd);
            i += cmd.Count;
        }
    }

    private static void InsertStopExtruding(List<string> lines, CodeEditorInjectSettings r, double speedMmS)
    {
        var cmd = PrepareCommand(r.StopExtrudingCommand, r.PointLoaderSafeIo);
        if (cmd.Count == 0)
            return;
        double offsetMm = CodeEditorInjectSettings.DistanceMm(r.StopUnits, r.StopDistance, speedMmS);
        bool before = CodeEditorInjectSettings.IsBefore(r.StopDirection);
        double tol = Math.Max(r.ToleranceMm, 0.0);

        for (int i = 0; i < lines.Count; i++)
        {
            if (IsIgnored(lines[i]) || !Starts(lines[i], ";travel start"))
                continue;
            int added = InsertTimed(lines, i, travelStartMarker: true, before, offsetMm, cmd, r.AlwaysInsert, tol);
            i += added;
        }
    }

    private static void InjectUrm(List<string> lines, CodeEditorInjectSettings r, double speedMmS)
    {
        var enter = PrepareCommand(r.EnterUrmCommand, r.PointLoaderSafeIo);
        var exit = PrepareCommand(r.ExitUrmCommand, r.PointLoaderSafeIo);
        if (enter.Count == 0 && exit.Count == 0)
            return;

        double enterMm = CodeEditorInjectSettings.DistanceMm(r.EnterUrmUnits, r.EnterUrmDistance, speedMmS);
        double exitMm = CodeEditorInjectSettings.DistanceMm(r.ExitUrmUnits, r.ExitUrmDistance, speedMmS);
        bool enterBefore = CodeEditorInjectSettings.IsBefore(r.EnterUrmDirection);
        bool exitBefore = CodeEditorInjectSettings.IsBefore(r.ExitUrmDirection);
        double tol = Math.Max(r.ToleranceMm, 0.0);

        var beads = new List<(int start, int end)>();
        int open = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (IsIgnored(lines[i]))
                continue;
            if (Starts(lines[i], ";travel end"))
                open = i;
            else if (Starts(lines[i], ";travel start") && open >= 0)
            {
                beads.Add((open, i));
                open = -1;
            }
        }

        for (int b = beads.Count - 1; b >= 0; b--)
        {
            var (start, end) = beads[b];
            var pts = CollectBead(lines, start, end);
            if (pts.Count < 2)
                continue;
            if (BeadLength(pts) <= enterMm + exitMm)
                continue;

            if (enter.Count > 0)
                InsertTimed(lines, end, travelStartMarker: true, enterBefore, enterMm, enter, r.AlwaysInsert, tol);
            if (exit.Count > 0)
                InsertTimed(lines, start, travelStartMarker: false, exitBefore, exitMm, exit, r.AlwaysInsert, tol);
        }

        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Contains("$OUT[7]", StringComparison.OrdinalIgnoreCase)
                && lines[i].Contains("FALSE", StringComparison.OrdinalIgnoreCase)
                && lines[i].Contains(ModifiedTag, StringComparison.Ordinal))
            {
                if (exit.Count > 0)
                    InsertBlock(lines, i + 1, exit);
                break;
            }
        }
    }

    /// <summary>
    /// Insert <paramref name="cmd"/> at a path offset from a travel marker.
    /// Before a <c>;travel start</c> walks back along the print bead.
    /// After a <c>;travel start</c> walks forward into the travel.
    /// Before a <c>;travel end</c> walks back along the travel.
    /// After a <c>;travel end</c> walks forward into the print bead.
    /// </summary>
    private static int InsertTimed(
        List<string> lines,
        int marker,
        bool travelStartMarker,
        bool before,
        double offsetMm,
        List<string> cmd,
        bool alwaysInsert,
        double tol)
    {
        int lo, hi;
        if (travelStartMarker)
        {
            if (before)
            {
                lo = FindPrev(lines, marker, ";travel end", ";travel start");
                hi = marker;
            }
            else
            {
                lo = marker;
                hi = FindNext(lines, marker, ";travel end", ";travel start");
            }
        }
        else
        {
            if (before)
            {
                lo = FindPrev(lines, marker, ";travel start", ";travel end");
                hi = marker;
            }
            else
            {
                lo = marker;
                hi = FindNext(lines, marker, ";travel start", ";travel end");
            }
        }

        int Fallback()
        {
            if (!alwaysInsert)
                return 0;
            int at = before ? marker : marker + 1;
            InsertBlock(lines, at, cmd);
            return cmd.Count;
        }

        if (lo < 0 || hi < 0)
            return Fallback();

        var pts = CollectBead(lines, lo, hi);
        if (pts.Count == 0)
            return Fallback();

        double length = BeadLength(pts);
        int insertAt = before ? hi : lo + 1;
        Target? extra = null;
        if (offsetMm > tol && length + tol >= offsetMm)
        {
            extra = before
                ? InterpolateFromEnd(pts, offsetMm, tol)
                : InterpolateFromStart(pts, offsetMm, tol);
            if (extra != null)
            {
                insertAt = extra.Value.LineIndex + 1;
                if (before && insertAt > hi)
                    insertAt = hi;
            }
            else
            {
                insertAt = before ? LastPointLine(pts) + 1 : FirstPointLine(pts);
                if (before && insertAt > hi)
                    insertAt = hi;
            }
        }
        else if (offsetMm > tol && !alwaysInsert)
        {
            return 0;
        }

        int added = 0;
        if (extra != null)
        {
            lines.Insert(insertAt, Tag(FormatLin(extra.Value, cVel: true)));
            added++;
            insertAt++;
        }
        InsertBlock(lines, insertAt, cmd);
        added += cmd.Count;
        return added;
    }

    private static int FindPrev(List<string> lines, int before, string want, string stop)
    {
        for (int i = before - 1; i >= 0; i--)
        {
            if (IsIgnored(lines[i]))
                continue;
            if (Starts(lines[i], want))
                return i;
            if (Starts(lines[i], stop))
                return -1;
        }
        return -1;
    }

    private static int FindNext(List<string> lines, int after, string want, string stop)
    {
        for (int i = after + 1; i < lines.Count; i++)
        {
            if (IsIgnored(lines[i]))
                continue;
            if (Starts(lines[i], want))
                return i;
            if (Starts(lines[i], stop))
                return -1;
        }
        return -1;
    }

    private readonly struct Target
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double Z { get; init; }
        public double A { get; init; }
        public double B { get; init; }
        public double C { get; init; }
        public double E1 { get; init; }
        public double E2 { get; init; }
        public double E3 { get; init; }
        public double E4 { get; init; }
        public double E5 { get; init; }
        public double E6 { get; init; }
        public int LineIndex { get; init; }
    }

    private static List<Target> CollectBead(List<string> lines, int start, int end)
    {
        var pts = new List<Target>();
        for (int i = start + 1; i < end; i++)
        {
            if (TryParseLin(lines[i], out var t))
                pts.Add(t with { LineIndex = i });
        }
        return pts;
    }

    private static double BeadLength(List<Target> pts)
    {
        double len = 0;
        for (int i = 1; i < pts.Count; i++)
            len += Dist(pts[i - 1], pts[i]);
        return len;
    }

    private static int LastPointLine(List<Target> pts) => pts.Count == 0 ? 0 : pts[^1].LineIndex;
    private static int FirstPointLine(List<Target> pts) => pts.Count == 0 ? 0 : pts[0].LineIndex;

    private static Target? InterpolateFromEnd(List<Target> pts, double offsetFromEndMm, double tol)
    {
        if (pts.Count < 2 || offsetFromEndMm <= tol)
            return null;
        double remain = offsetFromEndMm;
        for (int i = pts.Count - 1; i > 0; i--)
        {
            double seg = Dist(pts[i - 1], pts[i]);
            if (seg + 1e-9 < remain)
            {
                remain -= seg;
                continue;
            }
            if (Math.Abs(seg - remain) <= tol)
                return null;
            double t = 1.0 - (remain / seg);
            if (t <= 1e-9 || t >= 1.0 - 1e-9)
                return null;
            return Lerp(pts[i - 1], pts[i], t);
        }
        return null;
    }

    private static Target? InterpolateFromStart(List<Target> pts, double offsetFromStartMm, double tol)
    {
        if (pts.Count < 2 || offsetFromStartMm <= tol)
            return null;
        double remain = offsetFromStartMm;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double seg = Dist(pts[i], pts[i + 1]);
            if (seg + 1e-9 < remain)
            {
                remain -= seg;
                continue;
            }
            if (Math.Abs(seg - remain) <= tol)
                return null;
            double t = remain / seg;
            if (t <= 1e-9 || t >= 1.0 - 1e-9)
                return null;
            return Lerp(pts[i], pts[i + 1], t);
        }
        return null;
    }

    private static Target Lerp(Target a, Target b, double t) => new()
    {
        X = a.X + (b.X - a.X) * t,
        Y = a.Y + (b.Y - a.Y) * t,
        Z = a.Z + (b.Z - a.Z) * t,
        A = b.A, B = b.B, C = b.C,
        E1 = a.E1 + (b.E1 - a.E1) * t,
        E2 = a.E2 + (b.E2 - a.E2) * t,
        E3 = a.E3 + (b.E3 - a.E3) * t,
        E4 = a.E4 + (b.E4 - a.E4) * t,
        E5 = a.E5 + (b.E5 - a.E5) * t,
        E6 = a.E6 + (b.E6 - a.E6) * t,
        LineIndex = a.LineIndex,
    };

    private static bool TryParseLin(string line, out Target t)
    {
        t = default;
        var m1 = LinXyz.Match(line);
        var m2 = LinAbc.Match(line);
        var m3 = LinExt.Match(line);
        if (!m1.Success || !m2.Success || !m3.Success)
            return false;
        static bool P(Group g, out double v) =>
            double.TryParse(g.Value, CultureInfo.InvariantCulture, out v);
        if (!P(m1.Groups[1], out var x) || !P(m1.Groups[2], out var y) || !P(m1.Groups[3], out var z))
            return false;
        if (!P(m2.Groups[1], out var a) || !P(m2.Groups[2], out var b) || !P(m2.Groups[3], out var c))
            return false;
        if (!P(m3.Groups[1], out var e1) || !P(m3.Groups[2], out var e2) || !P(m3.Groups[3], out var e3)
            || !P(m3.Groups[4], out var e4) || !P(m3.Groups[5], out var e5) || !P(m3.Groups[6], out var e6))
            return false;
        t = new Target { X = x, Y = y, Z = z, A = a, B = b, C = c, E1 = e1, E2 = e2, E3 = e3, E4 = e4, E5 = e5, E6 = e6 };
        return true;
    }

    private static double Dist(Target a, Target b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static string FormatLin(Target t, bool cVel)
    {
        static string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("LIN {X ").Append(F3(t.X))
          .Append(", Y ").Append(F3(t.Y))
          .Append(", Z ").Append(F3(t.Z))
          .Append(", A ").Append(F3(t.A))
          .Append(", B ").Append(F3(t.B))
          .Append(", C ").Append(F3(t.C))
          .Append(", E1 ").Append(F3(t.E1))
          .Append(", E2 ").Append(F3(t.E2))
          .Append(", E3 ").Append(F3(t.E3))
          .Append(", E4 ").Append(F3(t.E4))
          .Append(", E5 ").Append(F3(t.E5))
          .Append(", E6 ").Append(F3(t.E6))
          .Append(" }");
        if (cVel)
            sb.Append(" C_VEL");
        return sb.ToString();
    }

    private static List<string> PrepareCommand(string? command, bool pointLoaderSafe)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(command))
            return result;
        foreach (var raw in command.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
                continue;
            if (pointLoaderSafe)
                line = TriggerDo.Replace(line, "");
            result.Add(Tag(line));
        }
        return result;
    }

    private static void InsertBlock(List<string> lines, int index, List<string> block)
    {
        if (index < 0)
            index = 0;
        if (index > lines.Count)
            index = lines.Count;
        lines.InsertRange(index, block);
    }

    private static string Tag(string line)
        => line.EndsWith(ModifiedTag, StringComparison.Ordinal) ? line : line + ModifiedTag;

    private static string TagIgnored(string line)
        => Tag(line.Contains(IgnoredPrefix, StringComparison.Ordinal) ? line : IgnoredPrefix + line);
}
