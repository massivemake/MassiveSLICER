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

    private static string? _resolvedGitExe;

    // Explorer-launched processes (a desktop shortcut, or the exe double-clicked directly)
    // can inherit a system/user PATH that never included git, even though an interactive dev
    // shell's PATH does -- confirmed on this exact machine (system/user PATH has no git entry
    // at all). Same root cause LaunchLatest.bat's build-time git invocation already had to
    // work around. Resolved once per process and cached: prefer plain "git" (respects
    // whatever PATH this process actually has), fall back to the well-known Git for Windows
    // install location if that's not resolvable at all.
    private static string ResolveGitExe()
    {
        if (_resolvedGitExe is not null) return _resolvedGitExe;
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            probe?.WaitForExit(2000);
            _resolvedGitExe = "git";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            const string fallback = @"C:\Program Files\Git\cmd\git.exe";
            _resolvedGitExe = File.Exists(fallback) ? fallback : "git";
        }
        return _resolvedGitExe;
    }

    private static string? RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo(ResolveGitExe())
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
