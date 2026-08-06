using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MassiveSlicer.App.Diagnostics;

/// <summary>
/// Timestamped diagnostics written to <c>%LOCALAPPDATA%\MassiveSlicer\logs</c>.
///
/// Two files are written in parallel: a per-run <c>session-yyyyMMdd-HHmmss.log</c> so old runs
/// stay separable, and <c>latest.log</c> so there is always one fixed path to ask an operator
/// for after a failed print. Old session files are pruned on startup.
///
/// Slice-pipeline entries include elapsed ms since the previous entry, so it is easy to see
/// where time went. Console lines are mirrored here too (see <see cref="Line"/>), and
/// <see cref="State"/> records the settings a slice or export actually ran with -- without
/// that, a log tells you the result but not the inputs that produced it.
/// </summary>
public static class SliceLogger
{
    /// <summary>Session files kept before the oldest are deleted.</summary>
    private const int KeepSessions = 30;

    private static readonly object Gate = new();
    private static readonly string LogDir;
    private static readonly string SessionPath;
    private static readonly string LatestPath;

    private static long _sessionStart;
    private static long _lastTick;
    private static int  _sessionId;

    static SliceLogger()
    {
        string dir;
        try
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MassiveSlicer", "logs");
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // Never let logging stop the app from starting.
            dir = Path.GetTempPath();
        }

        LogDir      = dir;
        SessionPath = Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        LatestPath  = Path.Combine(dir, "latest.log");

        try { File.WriteAllText(LatestPath, ""); } catch { }
        Prune();
        AdoptCrashLog();

        Write($"=== MassiveSlicer log start  {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Write($"    session file: {SessionPath}");
    }

    /// <summary>Folder holding the session logs. Surfaced by the 'logs' console command.</summary>
    public static string Directory_ => LogDir;

    /// <summary>The fixed path to hand to an operator: "send me this file".</summary>
    public static string LatestFile => LatestPath;

    public static void BeginSession(string label)
    {
        Interlocked.Increment(ref _sessionId);
        long now = Environment.TickCount64;
        _sessionStart = now;
        _lastTick     = now;
        Write($"=== SESSION {_sessionId} START: {label} ===");
    }

    public static void Step(string label)
    {
        long now     = Environment.TickCount64;
        long elapsed = now - Interlocked.Exchange(ref _lastTick, now);
        long total   = now - _sessionStart;
        Write($"  [{total,6} ms total | +{elapsed,5} ms]  {label}");
    }

    public static void EndSession(string label = "done")
    {
        long total = Environment.TickCount64 - _sessionStart;
        Write($"=== SESSION {_sessionId} END: {label} ({total} ms total) ===\n");
    }

    public static void Error(string label, Exception ex)
    {
        long total = Environment.TickCount64 - _sessionStart;
        Write($"  [{total,6} ms total]  ERROR at '{label}': {ex.GetType().Name}: {ex.Message}");
        Write($"  Stack: {ex.StackTrace}");
        Write($"=== SESSION {_sessionId} FAILED ===\n");
    }

    /// <summary>
    /// Mirrors a console line into the log so the operator-facing narrative survives the app
    /// closing. The in-app console is memory-only and disappears on exit.
    /// </summary>
    public static void Line(string text, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Write(isError ? $"! {text}" : $"  {text}");
    }

    /// <summary>
    /// Records the settings a slice or export actually ran with. This is the part that makes the
    /// file a record rather than a transcript -- results without inputs cannot be diagnosed later.
    /// </summary>
    public static void State(string what, IEnumerable<(string Key, string Value)> pairs)
    {
        var list = pairs as IList<(string Key, string Value)> ?? pairs.ToList();
        Write($"[state] {what}");
        foreach (var (k, v) in list)
            Write($"        {k,-18} {v}");
    }

    /// <summary>Keeps the newest <see cref="KeepSessions"/> session files.</summary>
    private static void Prune()
    {
        try
        {
            var old = new DirectoryInfo(LogDir)
                .GetFiles("session-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeepSessions)
                .ToList();
            foreach (var f in old) { try { f.Delete(); } catch { } }
        }
        catch { }
    }

    /// <summary>
    /// Copies the crash log out of %TEMP% into the log folder. Windows cleans %TEMP%, so crash
    /// evidence quietly evaporates otherwise -- and it is exactly what you want after a failure.
    /// </summary>
    private static void AdoptCrashLog()
    {
        try
        {
            var src = Path.Combine(Path.GetTempPath(), "massiveslicer-crash.log");
            if (!File.Exists(src)) return;
            var dst = Path.Combine(LogDir, $"crash-{File.GetLastWriteTime(src):yyyyMMdd-HHmmss}.log");
            if (!File.Exists(dst)) File.Copy(src, dst);
        }
        catch { }
    }

    private static void Write(string line)
    {
        string entry = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        lock (Gate)
        {
            try { File.AppendAllText(SessionPath, entry + "\n"); } catch { }
            try { File.AppendAllText(LatestPath,  entry + "\n"); } catch { }
        }
        System.Console.Error.WriteLine(entry);
    }
}
