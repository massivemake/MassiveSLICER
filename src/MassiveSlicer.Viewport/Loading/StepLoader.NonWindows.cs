using System.Diagnostics;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// STEP (.stp/.step) import for macOS/Linux. Open CASCADE's .NET binding (Occt.NET)
/// ships Windows-only natives, so non-Windows builds tessellate STEP through the
/// small OCCT Python wheel <c>cascadio</c> running in a private venv under
/// <c>Application Support/MassiveSlicer/step-env</c> — provisioned automatically on
/// first use (one-time <c>pip install cascadio</c>, needs network once).
///
/// The converter's GLB keeps the STEP's Z-up axes and rescales millimetres to glTF
/// metres — no axis swap and no node transforms (verified against OBJ output and the
/// raw STEP CARTESIAN_POINTs) — so the mesh loads with a plain ×1000 scale, matching
/// the Windows loader's Z-up-millimetre output.
/// </summary>
public static class StepLoader
{
    private static readonly string EnvDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MassiveSlicer", "step-env");

    private static string VenvPython => Path.Combine(EnvDir, "bin", "python3");

    /// <summary>Linear mesh deflection (mm) — matches the Windows Occt loader.</summary>
    private const double LinearDeflectionMm = 0.5;

    /// <summary>Angular deflection (radians) — matches the Windows Occt loader.</summary>
    private const double AngularDeflectionRad = 0.5;

    public static SceneNode Load(string path, string? name = null)
    {
        string python = EnsureConverter();
        string tmpGlb = Path.Combine(Path.GetTempPath(), $"msl-step-{Guid.NewGuid():N}.glb");
        try
        {
            Console.WriteLine($"[step] tessellating {Path.GetFileName(path)} (OCCT via cascadio)…");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Run(python,
                [
                    "-c",
                    "import sys, cascadio; cascadio.step_to_glb(sys.argv[1], sys.argv[2], "
                        + $"tol_linear={LinearDeflectionMm.ToString(inv)}, "
                        + $"tol_angular={AngularDeflectionRad.ToString(inv)})",
                    path, tmpGlb,
                ],
                TimeSpan.FromMinutes(10),
                $"STEP tessellation failed for '{Path.GetFileName(path)}'");

            var root = GltfLoader.Load(tmpGlb);
            // cascadio keeps Z-up and only rescales mm→m: replace the glTF loader's
            // Y-up→Z-up conversion with the plain metre→millimetre scale.
            root.LocalTransform = Matrix4.CreateScale(1000f);
            root.Name = name ?? Path.GetFileNameWithoutExtension(path);
            return root;
        }
        finally
        {
            try { if (File.Exists(tmpGlb)) File.Delete(tmpGlb); } catch { /* temp file */ }
        }
    }

    /// <summary>Returns the venv python with cascadio available, provisioning on first use.</summary>
    private static string EnsureConverter()
    {
        if (!File.Exists(VenvPython))
        {
            string sysPython = File.Exists("/usr/bin/python3") ? "/usr/bin/python3" : "python3";
            Console.WriteLine("[step] one-time setup: creating the STEP converter environment…");
            Run(sysPython, ["-m", "venv", EnvDir], TimeSpan.FromMinutes(3),
                "Could not create the STEP converter environment (python3 with venv support is required)");
        }

        if (!TryRun(VenvPython, ["-c", "import cascadio"], TimeSpan.FromSeconds(30)))
        {
            Console.WriteLine("[step] one-time setup: installing the OCCT STEP converter (pip install cascadio)…");
            Run(VenvPython, ["-m", "pip", "install", "--quiet", "cascadio"], TimeSpan.FromMinutes(5),
                $"Could not install the STEP converter — check network access or run: \"{VenvPython}\" -m pip install cascadio");
        }

        return VenvPython;
    }

    private static bool TryRun(string exe, string[] args, TimeSpan timeout)
    {
        try { Run(exe, args, timeout, "probe"); return true; }
        catch { return false; }
    }

    private static void Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, string errorContext)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new IOException($"{errorContext}: could not start '{exe}'.");

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"{errorContext}: timed out after {timeout.TotalMinutes:0} min.");
        }

        if (p.ExitCode != 0)
        {
            string err = stderr.GetAwaiter().GetResult().Trim();
            if (err.Length > 400) err = err[^400..];
            throw new IOException($"{errorContext}: {(err.Length > 0 ? err : $"exit code {p.ExitCode}")}");
        }

        _ = stdout.GetAwaiter().GetResult();
    }
}
