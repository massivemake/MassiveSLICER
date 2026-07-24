using System.Diagnostics;
using Avalonia.Threading;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App;

/// <summary>
/// One-shot, non-blocking check for whether origin/main has moved past this build's
/// baked-in <see cref="BuildInfo.Baseline"/>. Fired once at launch (see
/// <see cref="MassiveSlicer.ViewModels.MainWindowViewModel"/>'s constructor) and
/// re-runnable on demand via the <c>check-build-freshness</c> console command.
///
/// Deliberately fire-and-forget: never awaited by app startup, never delays the window
/// opening, and fails completely silently (no dialog, no log spam, no crash) if git or
/// the network isn't available — same defensive posture as the build-time
/// GenerateBuildInfo MSBuild target this mirrors.
/// </summary>
public static class BuildFreshnessChecker
{
    public static void CheckAsync(StatusBarViewModel statusBar)
    {
        _ = Task.Run(() =>
        {
            try
            {
                // Best-effort refresh of the local origin/main ref. If this fails (offline,
                // no credentials cached, whatever) the count below just falls back to
                // whatever origin/main was last fetched to — never blocks on failure.
                RunGit("fetch", "origin", "main", "--quiet");

                var freshBaseline = RunGitCount("rev-list", "--count", "origin/main");
                if (freshBaseline is null || freshBaseline.Value <= BuildInfo.Baseline) return;

                Post(() =>
                {
                    statusBar.LatestBaseline = freshBaseline.Value;
                    statusBar.IsBuildStale = true;
                });
            }
            catch
            {
                // Best-effort only. Never surface a failed freshness check to the user.
            }
        });
    }

    private static int? RunGitCount(params string[] args)
        => int.TryParse(RunGit(args)?.Trim(), out var n) ? n : null;

    private static string? RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Never let a fetch hang waiting on an interactive credential prompt.
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null) return null;

        var output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(5000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return null;
        }
        return proc.ExitCode == 0 ? output : null;
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
