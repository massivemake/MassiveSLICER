using System.Diagnostics;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Tessellates STEP (.stp / .step) via the open-source <c>cascadio</c> Python wheel
/// (Open CASCADE, LGPL) in a private venv under AppData. Used on all platforms —
/// on Windows this avoids the third-party Occt.NET license MessageBox that crashes
/// the host when dismissed incorrectly.
///
/// cascadio keeps Z-up and only rescales mm→m, so the returned node uses a plain
/// ×1000 scale (no Y-up→Z-up flip).
/// </summary>
internal static class CascadioStepConverter
{
    private static readonly string EnvDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MassiveSlicer", "step-env");

    private static string VenvPython => OperatingSystem.IsWindows()
        ? Path.Combine(EnvDir, "Scripts", "python.exe")
        : Path.Combine(EnvDir, "bin", "python3");

    /// <summary>Linear mesh deflection (mm).</summary>
    internal const double LinearDeflectionMm = 0.5;

    /// <summary>Angular deflection (radians).</summary>
    internal const double AngularDeflectionRad = 0.5;

    internal static SceneNode Load(string path, string? name = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("STEP file not found.", path);

        string python = EnsureConverter();
        string tmpGlb = Path.Combine(Path.GetTempPath(), $"msl-step-{Guid.NewGuid():N}.glb");
        try
        {
            Console.WriteLine($"[step] tessellating {Path.GetFileName(path)} via cascadio…");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Run(python,
                [
                    "-c",
                    "import sys, cascadio; cascadio.step_to_glb(sys.argv[1], sys.argv[2], "
                        + $"tol_linear={LinearDeflectionMm.ToString(inv)}, "
                        + $"tol_angular={AngularDeflectionRad.ToString(inv)})",
                    path, tmpGlb,
                ],
                TimeSpan.FromMinutes(15),
                $"STEP tessellation failed for '{Path.GetFileName(path)}'");

            if (!File.Exists(tmpGlb) || new FileInfo(tmpGlb).Length < 200)
                throw new InvalidDataException(
                    $"STEP converter produced no geometry for '{Path.GetFileName(path)}'. " +
                    "The file may be empty, assembly-only, or unsupported.");

            var root = GltfLoader.Load(tmpGlb);
            // cascadio keeps Z-up and only rescales mm→m: replace the glTF loader's
            // Y-up→Z-up conversion with the plain metre→millimetre scale.
            root.LocalTransform = Matrix4.CreateScale(1000f);
            root.Name = name ?? Path.GetFileNameWithoutExtension(path);
            root.SourceFilePath = Path.GetFullPath(path);

            if (!HasAnyMesh(root))
                throw new InvalidDataException(
                    $"STEP file '{Path.GetFileName(path)}' tessellated but contains no triangles.");

            return root;
        }
        finally
        {
            try { if (File.Exists(tmpGlb)) File.Delete(tmpGlb); } catch { /* temp file */ }
        }
    }

    static bool HasAnyMesh(SceneNode node)
    {
        if (node.PendingMesh is { Positions.Length: >= 9 }) return true;
        foreach (var child in node.Children)
            if (HasAnyMesh(child)) return true;
        return false;
    }

    /// <summary>Returns the venv python with cascadio available, provisioning on first use.</summary>
    private static string EnsureConverter()
    {
        if (!File.Exists(VenvPython))
        {
            string sysPython = ResolveSystemPython();
            Console.WriteLine("[step] one-time setup: creating the STEP converter environment…");
            Run(sysPython, ["-m", "venv", EnvDir], TimeSpan.FromMinutes(3),
                "Could not create the STEP converter environment (Python 3 with venv support is required)");
        }

        if (!TryRun(VenvPython, ["-c", "import cascadio, numpy"], TimeSpan.FromSeconds(45)))
        {
            Console.WriteLine("[step] one-time setup: installing cascadio + numpy…");
            Run(VenvPython, ["-m", "pip", "install", "--quiet", "numpy", "cascadio"], TimeSpan.FromMinutes(8),
                $"Could not install the STEP converter — check network access or run: \"{VenvPython}\" -m pip install numpy cascadio");
        }

        return VenvPython;
    }

    private static string ResolveSystemPython()
    {
        if (OperatingSystem.IsWindows())
        {
            // Prefer the py launcher, then python on PATH.
            foreach (var candidate in new[] { "py", "python", "python3" })
            {
                if (CommandExists(candidate))
                    return candidate == "py" ? "py" : candidate;
            }
            throw new IOException(
                "Python 3 is required for STEP import. Install from https://www.python.org/downloads/ " +
                "and ensure 'python' is on PATH, then re-import the .stp file.");
        }

        if (File.Exists("/usr/bin/python3")) return "/usr/bin/python3";
        if (CommandExists("python3")) return "python3";
        throw new IOException("python3 is required for STEP import.");
    }

    private static bool CommandExists(string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where.exe" : "which",
                Arguments = name,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool TryRun(string exe, string[] args, TimeSpan timeout)
    {
        try { Run(exe, args, timeout, "probe"); return true; }
        catch { return false; }
    }

    private static void Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, string errorContext)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // On Windows, `py -3` needs special handling — use python directly when possible.
        if (OperatingSystem.IsWindows() && exe.Equals("py", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "py";
            psi.ArgumentList.Add("-3");
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = exe;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)
            ?? throw new IOException($"{errorContext}: could not start '{exe}'.");

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"{errorContext}: timed out after {timeout.TotalMinutes:0.#} min.");
        }

        if (p.ExitCode != 0)
        {
            string err = stderr.GetAwaiter().GetResult().Trim();
            if (err.Length > 500) err = err[^500..];
            throw new IOException($"{errorContext}: {(err.Length > 0 ? err : $"exit code {p.ExitCode}")}");
        }

        _ = stdout.GetAwaiter().GetResult();
    }
}
