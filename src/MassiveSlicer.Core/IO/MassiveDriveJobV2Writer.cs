using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Writes a massivedrive.job/v2 directory: manifest.json, segments.bin, preview, summary.
/// </summary>
public static class MassiveDriveJobV2Writer
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MassiveDriveJobV2WriteResult Write(
        MassiveDriveJobBuild build,
        string jobDirectory,
        string relativeRoot,
        bool usedStaging,
        string? shareWarning,
        IProgress<MassiveDriveWriteProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (build.Segments.Count == 0)
            throw new InvalidOperationException("Toolpath has no exportable print/travel moves.");

        Directory.CreateDirectory(jobDirectory);
        string segmentsPath = Path.Combine(jobDirectory, MassiveDriveJobV2.SegmentsFileName);
        string previewPath = Path.Combine(jobDirectory, MassiveDriveJobV2.PreviewFileName);
        string summaryPath = Path.Combine(jobDirectory, MassiveDriveJobV2.SummaryFileName);
        string manifestPath = Path.Combine(jobDirectory, MassiveDriveJobV2.ManifestFileName);

        progress?.Report(new MassiveDriveWriteProgress("Writing segments.bin", 0, build.Segments.Count));
        var (sha, previewXyz, summary) = WriteSegmentsAndPreview(
            build.Segments, segmentsPath, progress);

        progress?.Report(new MassiveDriveWriteProgress("Writing preview", previewXyz.Count / 3, previewXyz.Count / 3));
        var previewDoc = new Dictionary<string, object?>
        {
            ["format"] = "massivedrive.preview/v1",
            ["count"] = previewXyz.Count / 3,
            ["xyz"] = previewXyz,
        };
        File.WriteAllText(previewPath, JsonSerializer.Serialize(previewDoc, JsonOpts));

        summary["sha256_segments"] = sha;
        summary["preview_points"] = previewXyz.Count / 3;
        progress?.Report(new MassiveDriveWriteProgress("Writing summary", 1, 1));
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, JsonOpts));

        var manifest = build.ToV2Manifest(build.Segments.Count, MassiveDriveSegmentBinary.RecordStride);
        progress?.Report(new MassiveDriveWriteProgress("Writing manifest", 1, 1));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts));

        var info = new FileInfo(segmentsPath);
        progress?.Report(new MassiveDriveWriteProgress("Done", build.Segments.Count, build.Segments.Count, $"{info.Length:N0} bytes"));

        return new MassiveDriveJobV2WriteResult
        {
            JobId = build.JobId,
            JobDirectory = jobDirectory,
            RelativeRoot = relativeRoot,
            ManifestPath = manifestPath,
            SegmentsPath = segmentsPath,
            PreviewPath = previewPath,
            SummaryPath = summaryPath,
            Sha256 = sha,
            SegmentCount = build.Segments.Count,
            PreviewPoints = previewXyz.Count / 3,
            SegmentsBytes = info.Length,
            UsedStaging = usedStaging,
            ShareWarning = shareWarning,
        };
    }

    public static MassiveDriveJobV2WriteResult WriteToShare(
        MassiveDriveJobBuild build,
        string? primaryRoot,
        string? stagingRoot = null,
        IProgress<MassiveDriveWriteProgress>? progress = null)
    {
        progress?.Report(new MassiveDriveWriteProgress("Preparing job folder", 0, 0, primaryRoot));
        var share = MassiveDriveJobShare.PrepareJobDirectory(build.JobId, primaryRoot, stagingRoot);
        return Write(build, share.JobDirectory, share.RelativeRoot, share.UsedStaging, share.Warning, progress);
    }

    static (string sha256, List<double> previewXyz, Dictionary<string, object?> summary)
        WriteSegmentsAndPreview(
            IReadOnlyList<MassiveDriveSegment> segments,
            string segmentsPath,
            IProgress<MassiveDriveWriteProgress>? progress)
    {
        int n = segments.Count;
        int previewBudget = TargetPreviewPoints(n);
        int endpointCount = n * 2;
        int stride = Math.Max(1, (endpointCount + previewBudget - 1) / previewBudget);

        var preview = new List<double>(previewBudget * 3 + 12);
        int printN = 0, travelN = 0, millN = 0, wipeN = 0;
        int layerMin = int.MaxValue, layerMax = int.MinValue;
        double printLen = 0, travelLen = 0, millLen = 0, duration = 0;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;

        using var fs = new FileStream(
            segmentsPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> header = stackalloc byte[MassiveDriveSegmentBinary.HeaderSize];
        MassiveDriveSegmentBinary.WriteHeader(header, n);
        fs.Write(header);
        hasher.AppendData(header);

        Span<byte> rec = stackalloc byte[MassiveDriveSegmentBinary.RecordStride];
        int endpointIndex = 0;
        for (int i = 0; i < n; i++)
        {
            var seg = segments[i];
            MassiveDriveSegmentBinary.WriteRecord(rec, seg);
            fs.Write(rec);
            hasher.AppendData(rec);

            Accumulate(seg, ref printN, ref travelN, ref millN, ref wipeN,
                ref layerMin, ref layerMax, ref printLen, ref travelLen, ref millLen,
                ref duration, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);

            SamplePreview(preview, seg.From, endpointIndex++, stride, force: i == 0);
            SamplePreview(preview, seg.To, endpointIndex++, stride, force: i == n - 1);

            if (progress is not null && ((i & 0x7FFF) == 0 || i == n - 1))
                progress.Report(new MassiveDriveWriteProgress("Writing segments.bin", i + 1, n));
        }

        fs.Flush();
        string sha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();

        if (layerMin == int.MaxValue) { layerMin = 0; layerMax = 0; }

        var summary = new Dictionary<string, object?>
        {
            ["segment_count"] = n,
            ["print_count"] = printN,
            ["travel_count"] = travelN,
            ["mill_count"] = millN,
            ["wipe_count"] = wipeN,
            ["layer_min"] = layerMin,
            ["layer_max"] = layerMax,
            ["bounds"] = new Dictionary<string, object?>
            {
                ["min"] = Xyz(minX, minY, minZ),
                ["max"] = Xyz(maxX, maxY, maxZ),
            },
            ["length_mm"] = new Dictionary<string, double>
            {
                ["print"] = Math.Round(printLen, 3),
                ["travel"] = Math.Round(travelLen, 3),
                ["mill"] = Math.Round(millLen, 3),
                ["total"] = Math.Round(printLen + travelLen + millLen, 3),
            },
            ["est_duration_s"] = Math.Round(duration, 3),
        };

        return (sha, preview, summary);
    }

    static int TargetPreviewPoints(int segmentCount)
    {
        if (segmentCount <= 0) return 0;
        int raw = Math.Min(segmentCount * 2, MassiveDriveJobV2.PreviewTargetPoints);
        return Math.Clamp(raw, 1, MassiveDriveJobV2.PreviewMaxPoints);
    }

    static void SamplePreview(List<double> xyz, MassiveDrivePose p, int endpointIndex, int stride, bool force)
    {
        if (!force && endpointIndex % stride != 0) return;
        if (xyz.Count >= MassiveDriveJobV2.PreviewMaxPoints * 3 && !force) return;
        xyz.Add(Math.Round(p.X, 3));
        xyz.Add(Math.Round(p.Y, 3));
        xyz.Add(Math.Round(p.Z, 3));
    }

    static void Accumulate(
        MassiveDriveSegment seg,
        ref int printN, ref int travelN, ref int millN, ref int wipeN,
        ref int layerMin, ref int layerMax,
        ref double printLen, ref double travelLen, ref double millLen,
        ref double duration,
        ref double minX, ref double minY, ref double minZ,
        ref double maxX, ref double maxY, ref double maxZ)
    {
        layerMin = Math.Min(layerMin, seg.Layer);
        layerMax = Math.Max(layerMax, seg.Layer);
        Expand(seg.From, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
        Expand(seg.To, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
        double dx = seg.To.X - seg.From.X, dy = seg.To.Y - seg.From.Y, dz = seg.To.Z - seg.From.Z;
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (seg.Wipe) wipeN++;
        switch (seg.Kind)
        {
            case "travel":
                travelN++;
                travelLen += len;
                break;
            case "mill":
                millN++;
                millLen += len;
                break;
            default:
                printN++;
                printLen += len;
                break;
        }
        if (seg.SpeedMmS > 0.05)
            duration += len / seg.SpeedMmS;
    }

    static void Expand(
        MassiveDrivePose p,
        ref double minX, ref double minY, ref double minZ,
        ref double maxX, ref double maxY, ref double maxZ)
    {
        minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
        maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
    }

    static Dictionary<string, double> Xyz(double x, double y, double z) => new()
    {
        ["x"] = double.IsFinite(x) ? Math.Round(x, 3) : 0,
        ["y"] = double.IsFinite(y) ? Math.Round(y, 3) : 0,
        ["z"] = double.IsFinite(z) ? Math.Round(z, 3) : 0,
    };
}
