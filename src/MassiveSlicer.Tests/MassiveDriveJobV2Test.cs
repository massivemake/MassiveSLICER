using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class MassiveDriveJobV2Test
{
    static Toolpath SamplePath()
    {
        var tp = new Toolpath();
        var layer0 = new ToolpathLayer(0, 10f)
        {
            Height = 3f,
            PlaneNormal = Vector3.UnitZ,
        };
        layer0.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        layer0.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(100, 50, 13), MoveKind.Travel)
        {
            IsLayerChange = true,
        });
        tp.Layers.Add(layer0);
        return tp;
    }

    static MassiveDriveExportSettings Settings(string name = "v2", int tool = 1, int @base = 6) => new()
    {
        Name = name,
        CellId = "lfam3",
        JobId = "testid123456",
        PrintSpeedMmS = 50f,
        TravelSpeedMmS = 120f,
        Tool = tool,
        Base = @base,
        SourceNote = "cell=LFAM 3 T1 B6 BASE",
    };

    [Fact]
    public void Policy_keeps_legacy_json_for_small_jobs_only()
    {
        Assert.True(MassiveDriveSendPolicy.UseLegacyJson(3, forceLegacy: false));
        Assert.True(MassiveDriveSendPolicy.UseLegacyJson(7_999, forceLegacy: false));
        Assert.False(MassiveDriveSendPolicy.UseLegacyJson(8_000, forceLegacy: false));
        Assert.False(MassiveDriveSendPolicy.UseLegacyJson(1_600_000, forceLegacy: false));
        Assert.True(MassiveDriveSendPolicy.UseLegacyJson(1_600_000, forceLegacy: true));
        Assert.True(MassiveDriveSendPolicy.EstimateJsonBytes(1_600_000) > 300_000_000);
    }

    [Fact]
    public void Pointer_payload_is_tiny_and_has_no_segment_array()
    {
        var pointer = new MassiveDrivePointerPayload
        {
            JobId = "testid123456",
            Name = "Curtain",
            Root = "testid123456",
            Sha256 = new string('a', 64),
        };
        var dict = pointer.ToDict();
        Assert.Equal(MassiveDriveJobV2.Format, dict["format"]);
        Assert.Equal("testid123456", dict["job_id"]);
        Assert.Equal("Curtain", dict["name"]);
        Assert.Equal("testid123456", dict["root"]);
        Assert.Equal(64, ((string)dict["sha256"]!).Length);
        Assert.False(dict.ContainsKey("segments"));

        var json = JsonSerializer.Serialize(dict);
        Assert.DoesNotContain("\"segments\"", json, StringComparison.Ordinal);
        Assert.InRange(json.Length, 1, 2048);
    }

    [Fact]
    public void Binary_writer_round_trips_fixed_stride_le_records()
    {
        var build = MassiveDriveJobExporter.Export(SamplePath(), Settings());
        Assert.True(build.Segments.Count >= 2);

        Span<byte> header = stackalloc byte[MassiveDriveSegmentBinary.HeaderSize];
        MassiveDriveSegmentBinary.WriteHeader(header, build.Segments.Count);
        var (version, stride, count) = MassiveDriveSegmentBinary.ReadHeader(header);
        Assert.Equal(2, version);
        Assert.Equal(80, stride);
        Assert.Equal(build.Segments.Count, count);
        Assert.Equal((byte)'M', header[0]);
        Assert.Equal((byte)'D', header[1]);
        Assert.Equal((byte)'S', header[2]);

        Span<byte> rec = stackalloc byte[MassiveDriveSegmentBinary.RecordStride];
        MassiveDriveSegmentBinary.WriteRecord(rec, build.Segments[0]);
        var back = MassiveDriveSegmentBinary.ReadRecord(rec);
        Assert.Equal(0, back.Index);
        Assert.Equal("print", back.Kind);
        Assert.Equal(0, back.Layer);
        Assert.Equal(build.Segments[0].From.X, back.From.X, 3);
        Assert.Equal(build.Segments[0].To.X, back.To.X, 3);
        Assert.Equal(build.Segments[0].SpeedMmS, back.SpeedMmS, 3);

        MassiveDriveSegmentBinary.WriteRecord(rec, build.Segments[1]);
        var travel = MassiveDriveSegmentBinary.ReadRecord(rec);
        Assert.Equal("travel", travel.Kind);
        Assert.True(travel.Reverse);
        Assert.True(travel.LayerChange);
        Assert.Equal(MassiveDriveSegmentBinary.KindTravel, rec[4]);
        Assert.Equal(
            MassiveDriveSegmentBinary.FlagReverse | MassiveDriveSegmentBinary.FlagLayerChange,
            rec[5]);
        Assert.True(BitConverter.IsLittleEndian);
    }

    [Fact]
    public void V2_directory_writes_manifest_segments_preview_summary_and_matching_sha256()
    {
        var build = MassiveDriveJobExporter.Export(SamplePath(), Settings(tool: 1, @base: 6));
        string root = Path.Combine(Path.GetTempPath(), "ms-drive-v2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = MassiveDriveJobV2Writer.WriteToShare(build, root);
            Assert.False(written.UsedStaging);
            Assert.Equal("testid123456", written.JobId);
            Assert.Equal("testid123456", written.RelativeRoot);
            Assert.True(File.Exists(written.ManifestPath));
            Assert.True(File.Exists(written.SegmentsPath));
            Assert.True(File.Exists(written.PreviewPath));
            Assert.True(File.Exists(written.SummaryPath));

            var bytes = File.ReadAllBytes(written.SegmentsPath);
            Assert.Equal(
                MassiveDriveSegmentBinary.HeaderSize
                + build.Segments.Count * MassiveDriveSegmentBinary.RecordStride,
                bytes.Length);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                written.Sha256);

            using var manifest = JsonDocument.Parse(File.ReadAllText(written.ManifestPath));
            var m = manifest.RootElement;
            Assert.Equal(MassiveDriveJobV2.Format, m.GetProperty("format").GetString());
            Assert.False(m.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array);
            Assert.Equal(1, m.GetProperty("frames").GetProperty("tool").GetInt32());
            Assert.Equal(6, m.GetProperty("frames").GetProperty("base").GetInt32());
            Assert.Equal("MassiveSLICER", m.GetProperty("source").GetProperty("app").GetString());
            Assert.Contains("B6", m.GetProperty("source").GetProperty("note").GetString());
            Assert.Equal(50, m.GetProperty("defaults").GetProperty("print_speed_mm_s").GetDouble());

            var pointer = written.Pointer(build.Name);
            Assert.Equal(written.Sha256, pointer.Sha256);
            Assert.False(pointer.ToDict().ContainsKey("segments"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Preview_downsamples_large_export_into_10_to_20k_xyz()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        for (int i = 0; i < 25_000; i++)
        {
            layer.Moves.Add(new ToolpathMove(
                new Vector3(i, 0, 3), new Vector3(i + 1, 0, 3), MoveKind.Extrude)
            {
                Normal = Vector3.UnitZ,
                PrintSpeedScale = 1f,
            });
        }
        tp.Layers.Add(layer);

        var build = MassiveDriveJobExporter.Export(tp, Settings("big"));
        Assert.Equal(25_000, build.Segments.Count);
        Assert.False(MassiveDriveSendPolicy.UseLegacyJson(build.Segments.Count, forceLegacy: false));

        string root = Path.Combine(Path.GetTempPath(), "ms-drive-v2-big-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = MassiveDriveJobV2Writer.WriteToShare(build, root);
            Assert.InRange(written.PreviewPoints, MassiveDriveJobV2.PreviewMinPoints, MassiveDriveJobV2.PreviewMaxPoints);
            using var preview = JsonDocument.Parse(File.ReadAllText(written.PreviewPath));
            Assert.Equal(written.PreviewPoints, preview.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(written.PreviewPoints * 3, preview.RootElement.GetProperty("xyz").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Share_falls_back_to_staging_when_primary_root_is_not_writable()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "ms-drive-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string blocked = Path.Combine(tmp, "blocked");
        File.WriteAllText(blocked, "not-a-directory");
        string staging = Path.Combine(tmp, "stage");
        try
        {
            var resolved = MassiveDriveJobShare.PrepareJobDirectory("jobabc123456", blocked, staging);
            Assert.True(resolved.UsedStaging);
            Assert.False(string.IsNullOrWhiteSpace(resolved.Warning));
            Assert.Contains("massive", resolved.Warning, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(resolved.JobDirectory));
            Assert.Equal("jobabc123456", resolved.RelativeRoot);
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task Client_posts_pointer_to_package_pointer_without_segments()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new MassiveDriveClient("http://192.168.0.201:8080", http: http);

        var pointer = new MassiveDrivePointerPayload
        {
            JobId = "pointerjob001",
            Name = "Curtain",
            Root = "pointerjob001",
            Sha256 = new string('b', 64),
        };

        using var doc = await client.UploadPackagePointerAsync(pointer);
        Assert.Equal("pkg-test", doc.RootElement.GetProperty("package_id").GetString());
        Assert.Single(handler.Requests);
        var (method, uri, body) = handler.Requests[0];
        Assert.Equal("POST", method);
        Assert.Contains("/api/jobs/package/pointer", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/jobs/package\"", uri, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(body));
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("massivedrive.job/v2", sent.RootElement.GetProperty("format").GetString());
        Assert.Equal("pointerjob001", sent.RootElement.GetProperty("job_id").GetString());
        Assert.Equal("pointerjob001", sent.RootElement.GetProperty("root").GetString());
        Assert.False(sent.RootElement.TryGetProperty("segments", out _));
        Assert.InRange(Encoding.UTF8.GetByteCount(body!), 1, 2048);
    }

    [Fact]
    public void Lfam3_cell_json_documents_drive_jobs_share()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine("assets", "cells", "LFAM3", "lfam3.json")),
            Path.GetFullPath(Path.Combine("src", "assets", "cells", "LFAM3", "lfam3.json")),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
            return;

        var cell = CellLoader.Load(path);
        Assert.False(string.IsNullOrWhiteSpace(cell.MassiveDriveJobsRoot));
        Assert.Contains("MassiveDRIVE", cell.MassiveDriveJobsRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("jobs", cell.MassiveDriveJobsRoot, StringComparison.OrdinalIgnoreCase);
    }

    sealed class RecordingHandler : HttpMessageHandler
    {
        public readonly List<(string method, string uri, string? body)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"package_id":"pkg-test"}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
