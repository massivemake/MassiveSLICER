namespace MassiveSlicer.Core.IO;

/// <summary>
/// massivedrive.job/v2 on-disk package + pointer POST.
/// Layout is documented in <c>docs/massivedrive-job-v2.md</c> — keep stable for Drive.
/// </summary>
public static class MassiveDriveJobV2
{
    public const string Format = "massivedrive.job/v2";
    public const string PointerApiPath = "api/jobs/package/pointer";
    public const string SegmentsFileName = "segments.bin";
    public const string ManifestFileName = "manifest.json";
    public const string PreviewFileName = "preview.json";
    public const string SummaryFileName = "summary.json";
    public const string SegmentsMagicAscii = "MDSEG2";
    public const int PreviewTargetPoints = 16_000;
    public const int PreviewMinPoints = 10_000;
    public const int PreviewMaxPoints = 20_000;
}

/// <summary>Tiny HTTP body for <c>POST /api/jobs/package/pointer</c> — no segment array.</summary>
public sealed record MassiveDrivePointerPayload
{
    public required string JobId { get; init; }
    public required string Name { get; init; }
    /// <summary>Path relative to the Drive jobs root (usually just <see cref="JobId"/>).</summary>
    public required string Root { get; init; }
    /// <summary>SHA-256 (hex) of <c>segments.bin</c>.</summary>
    public required string Sha256 { get; init; }

    public Dictionary<string, object?> ToDict() => new()
    {
        ["format"] = MassiveDriveJobV2.Format,
        ["job_id"] = JobId,
        ["name"] = Name,
        ["root"] = Root,
        ["sha256"] = Sha256,
    };
}

/// <summary>When to keep the legacy v1 JSON POST versus the v2 directory + pointer.</summary>
public static class MassiveDriveSendPolicy
{
    public const int LegacyMaxSegments = 8_000;
    public const long LegacyMaxJsonBytes = 4L * 1024 * 1024;
    public const int EstimatedJsonBytesPerSegment = 220;

    public static long EstimateJsonBytes(int segmentCount)
        => (long)Math.Max(0, segmentCount) * EstimatedJsonBytesPerSegment;

    /// <summary>
    /// Legacy JSON is for small jobs (or an explicit debug flag). Large jobs must
    /// not put the segment array on the wire.
    /// </summary>
    public static bool UseLegacyJson(int segmentCount, bool forceLegacy)
    {
        if (forceLegacy) return true;
        if (segmentCount >= LegacyMaxSegments) return false;
        return EstimateJsonBytes(segmentCount) < LegacyMaxJsonBytes;
    }
}

public readonly record struct MassiveDriveWriteProgress(
    string Phase, int Completed, int Total, string? Detail = null)
{
    public override string ToString()
    {
        if (Total > 0)
            return string.IsNullOrEmpty(Detail)
                ? $"{Phase} {Completed:N0}/{Total:N0}"
                : $"{Phase} {Completed:N0}/{Total:N0} — {Detail}";
        return string.IsNullOrEmpty(Detail) ? Phase : $"{Phase} — {Detail}";
    }
}

public sealed record MassiveDriveJobV2WriteResult
{
    public required string JobId { get; init; }
    public required string JobDirectory { get; init; }
    public required string RelativeRoot { get; init; }
    public required string ManifestPath { get; init; }
    public required string SegmentsPath { get; init; }
    public required string PreviewPath { get; init; }
    public required string SummaryPath { get; init; }
    public required string Sha256 { get; init; }
    public required int SegmentCount { get; init; }
    public required int PreviewPoints { get; init; }
    public required long SegmentsBytes { get; init; }
    public required bool UsedStaging { get; init; }
    public string? ShareWarning { get; init; }

    public MassiveDrivePointerPayload Pointer(string name) => new()
    {
        JobId = JobId,
        Name = name,
        Root = RelativeRoot,
        Sha256 = Sha256,
    };
}

public sealed record MassiveDriveShareResolveResult
{
    public required string JobDirectory { get; init; }
    public required string RelativeRoot { get; init; }
    public required string UsedRoot { get; init; }
    public required bool UsedStaging { get; init; }
    public string? PrimaryError { get; init; }
    public string? Warning { get; init; }
}

/// <summary>Resolves the Drive-visible jobs share, with local staging if UNC/write fails.</summary>
public static class MassiveDriveJobShare
{
    public static string DefaultStagingRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MassiveSlicer", "drive-jobs");

    public static string ShareCredentialHint =>
        "Samba share currently allows user 'massive'. The shop PC may need those credentials "
        + "or a writable jobs share (UNC \\\\192.168.0.201\\MassiveDRIVE\\var\\jobs or a mapped path).";

    /// <summary>
    /// Create <c>{root}/{jobId}</c>. Prefer <paramref name="primaryRoot"/> (cell/prefs share).
    /// On macOS, a Windows UNC for the LFAM 3 jobs share is not a path, so the usual
    /// mounts are tried before local staging. If every share write fails, fall back
    /// to staging and return a warning.
    /// </summary>
    public static MassiveDriveShareResolveResult PrepareJobDirectory(
        string jobId,
        string? primaryRoot,
        string? stagingRoot = null,
        IReadOnlyList<string>? extraRoots = null)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("job id is required", nameof(jobId));
        if (jobId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("job id is not a valid folder name: " + jobId, nameof(jobId));

        string relative = jobId;
        string? primaryError = null;

        foreach (var root in RootsToTry(primaryRoot, extraRoots))
        {
            var attempt = TryCreate(root, jobId);
            if (attempt.ok)
            {
                return new MassiveDriveShareResolveResult
                {
                    JobDirectory = attempt.dir!,
                    RelativeRoot = relative,
                    UsedRoot = root,
                    UsedStaging = false,
                };
            }

            primaryError ??= attempt.error;
        }

        string staging = string.IsNullOrWhiteSpace(stagingRoot)
            ? DefaultStagingRoot()
            : stagingRoot.Trim();
        var staged = TryCreate(staging, jobId);
        if (!staged.ok)
        {
            var shareBit = primaryError is null
                ? "No MassiveDRIVE jobs share is configured (set massiveDriveJobsRoot on the cell JSON or MassiveDriveJobsRoot in prefs)."
                : $"Share write failed at '{primaryRoot}': {primaryError}.";
            throw new IOException(
                $"{shareBit} Local staging at '{staging}' also failed: {staged.error} {ShareCredentialHint}");
        }

        string warning = primaryError is null
            ? $"No MassiveDRIVE jobs share configured — wrote local staging at {staged.dir}. "
              + "Drive cannot see this path until massiveDriveJobsRoot points at a share the Drive host mounts. "
              + ShareCredentialHint
            : $"Share write denied at '{primaryRoot}': {primaryError}. "
              + $"Wrote a local copy at {staged.dir}. Drive will not see this folder on its disk. "
              + ShareCredentialHint;

        return new MassiveDriveShareResolveResult
        {
            JobDirectory = staged.dir!,
            RelativeRoot = relative,
            UsedRoot = staging,
            UsedStaging = true,
            PrimaryError = primaryError,
            Warning = warning,
        };
    }

    /// <summary>
    /// Share roots to try before staging. <paramref name="extraRoots"/> is for tests.
    /// When it is null and this is not Windows, the LFAM 3 UNC also tries the Mac mounts.
    /// </summary>
    static IEnumerable<string> RootsToTry(string? primaryRoot, IReadOnlyList<string>? extraRoots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(primaryRoot))
        {
            string trimmed = primaryRoot.Trim();
            // A Windows UNC is not a path on macOS. Creating it would make a junk
            // folder whose name contains backslashes, not the Drive jobs share.
            if (OperatingSystem.IsWindows() || !IsWindowsUnc(trimmed))
            {
                if (seen.Add(trimmed))
                    yield return trimmed;
            }
        }

        if (extraRoots is not null)
        {
            foreach (var extra in extraRoots)
            {
                if (!string.IsNullOrWhiteSpace(extra) && seen.Add(extra.Trim()))
                    yield return extra.Trim();
            }
            yield break;
        }

        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(primaryRoot))
            yield break;

        foreach (var mount in MacMountsForUnc(primaryRoot))
        {
            if (seen.Add(mount))
                yield return mount;
        }
    }

    /// <summary>Known macOS mounts for <c>\\192.168.0.201\MassiveDRIVE\var\jobs</c>.</summary>
    internal static IEnumerable<string> MacMountsForUnc(string root)
    {
        string norm = root.Replace('/', '\\').Trim().TrimEnd('\\');
        if (!norm.StartsWith(@"\\", StringComparison.Ordinal))
            yield break;
        const string jobs = @"\MassiveDRIVE\var\jobs";
        if (!norm.EndsWith(jobs, StringComparison.OrdinalIgnoreCase))
            yield break;
        yield return "/Users/massive/mnt/massivedrive/var/jobs";
        yield return "/Volumes/MassiveDRIVE/var/jobs";
    }

    static bool IsWindowsUnc(string root)
    {
        string norm = root.Replace('/', '\\').Trim();
        return norm.StartsWith(@"\\", StringComparison.Ordinal);
    }

    static (bool ok, string? dir, string? error) TryCreate(string root, string jobId)
    {
        if (!OperatingSystem.IsWindows() && root.Contains('\\'))
            return (false, null, "Windows UNC is not a path on this Mac.");
        try
        {
            string dir = Path.Combine(root, jobId);
            Directory.CreateDirectory(dir);
            // Prove the share is writable (UNC can create a dir then fail the first file).
            string probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return (true, dir, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return (false, null, ex.Message);
        }
    }
}
