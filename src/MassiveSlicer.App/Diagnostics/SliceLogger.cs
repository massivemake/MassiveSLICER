using System;
using System.IO;
using System.Threading;

namespace MassiveSlicer.App.Diagnostics;

/// <summary>
/// Writes timestamped slice-pipeline diagnostics to ~/Desktop/massiveslicer-slice.log.
/// Each entry includes elapsed ms since the last entry so it's easy to spot where time is spent.
/// </summary>
public static class SliceLogger
{
    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                     "massiveslicer-slice.log");

    private static long _sessionStart;
    private static long _lastTick;
    private static int  _sessionId;

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

    private static void Write(string line)
    {
        string entry = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        try { File.AppendAllText(LogPath, entry + "\n"); }
        catch { /* never throw from logger */ }
        System.Console.Error.WriteLine(entry);
    }
}
