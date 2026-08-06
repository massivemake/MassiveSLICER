using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// HTTP client for MassiveDRIVE job package upload + path executor start.
/// Stdlib-friendly; used by "Send to MassiveDRIVE — {cell}".
/// </summary>
public sealed class MassiveDriveClient : IDisposable
{
    readonly HttpClient _http;
    readonly bool _owns;

    public MassiveDriveClient(string baseUrl, TimeSpan? timeout = null, HttpClient? http = null)
    {
        BaseUrl = baseUrl.TrimEnd('/') + "/";
        if (http is not null)
        {
            _http = http;
            _owns = false;
        }
        else
        {
            // Motion APIs (axes/bulk) can block up to ~120s; default timeout covers that.
            _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(3) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MassiveSLICER-MassiveDriveClient/0.2");
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _owns = true;
        }
    }

    public string BaseUrl { get; }

    public async Task<JsonDocument> HealthAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(new Uri(new Uri(BaseUrl), "api/health"), ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    public async Task<JsonDocument> UploadPackageAsync(
        Dictionary<string, object?> package,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(package);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(new Uri(new Uri(BaseUrl), "api/jobs/package"), content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new MassiveDriveClientException((int)resp.StatusCode, body);
        return JsonDocument.Parse(body);
    }

    public async Task<JsonDocument> StartPackageAsync(
        string packageId,
        string? name = null,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["package_id"] = packageId,
            ["name"] = name ?? packageId,
            ["dry_run"] = dryRun,
        };
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var path = dryRun ? "api/executor/dry-run" : "api/executor/start";
        using var resp = await _http.PostAsync(new Uri(new Uri(BaseUrl), path), content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new MassiveDriveClientException((int)resp.StatusCode, body);
        return JsonDocument.Parse(body);
    }

    public async Task<JsonDocument> ExecutorStateAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(new Uri(new Uri(BaseUrl), "api/executor"), ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>
    /// Live robot snapshot from MassiveDRIVE (<c>GET /api/robot?force=1</c>), including
    /// <c>axes</c> A1–A6/E1 when the brain can read <c>$AXIS_ACT</c> via KVP.
    /// </summary>
    public async Task<JsonDocument> RobotStatusAsync(bool force = true, CancellationToken ct = default)
    {
        var path = force ? "api/robot?force=1" : "api/robot";
        using var resp = await _http.GetAsync(new Uri(new Uri(BaseUrl), path), ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>
    /// Joint angles [A1..A6, E1] from MassiveDRIVE robot status, or null if unavailable.
    /// </summary>
    public async Task<double[]?> ReadAxesAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await RobotStatusAsync(force: true, ct);
            var root = doc.RootElement;
            if (!root.TryGetProperty("robot", out var robot))
                robot = root;
            if (!robot.TryGetProperty("axes", out var axes) || axes.ValueKind != JsonValueKind.Object)
                return null;

            double Get(string k)
            {
                if (!axes.TryGetProperty(k, out var p)) return double.NaN;
                return p.ValueKind == JsonValueKind.Number ? p.GetDouble() : double.NaN;
            }

            var a = new double[7];
            a[0] = Get("a1"); a[1] = Get("a2"); a[2] = Get("a3");
            a[3] = Get("a4"); a[4] = Get("a5"); a[5] = Get("a6");
            a[6] = Get("e1");
            if (double.IsNaN(a[6])) a[6] = 0;
            if (a.Take(6).Any(double.IsNaN))
                return null;
            return a;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the MassiveDRIVE path executor is currently moving the robot (RSI path).
    /// Used by MassiveSLICER bed/scan calibration to avoid fighting CELL <c>MS_*</c> motion.
    /// Unreachable DRIVE is treated as non-blocking (cal can proceed offline).
    /// </summary>
    public async Task<MassiveDrivePathStatus> QueryPathStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await ExecutorStateAsync(ct);
            return MassiveDrivePathGate.ParseExecutorJson(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or MassiveDriveClientException
                                       or JsonException or InvalidOperationException)
        {
            return MassiveDrivePathGate.Unreachable(ex.Message);
        }
    }

    public async Task<JsonDocument> StopAsync(string reason = "slicer", CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { reason });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(new Uri(new Uri(BaseUrl), "api/executor/stop"), content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new MassiveDriveClientException((int)resp.StatusCode, body);
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Joint PTP via MassiveDRIVE <c>POST /api/motion/axes</c> (BulkPTP <c>MS_CMD=93</c>).
    /// Requires <c>LFAM3_RSI_BulkPTP</c> REL 32+ in MOVECORR hold.
    /// </summary>
    public async Task<JsonDocument> MoveAxesAsync(
        double a1, double a2, double a3, double a4, double a5, double a6, double e1 = 0,
        int velPct = 20, int? tool = null, int? baseIndex = null,
        double tolDeg = 1.5, double waitS = 120, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["a1"] = a1, ["a2"] = a2, ["a3"] = a3,
            ["a4"] = a4, ["a5"] = a5, ["a6"] = a6,
            ["e1"] = e1,
            ["vel_pct"] = Math.Clamp(velPct, 1, 100),
            ["tol_deg"] = tolDeg,
            ["wait_s"] = waitS,
        };
        if (tool is int t) payload["tool"] = t;
        if (baseIndex is int b) payload["base"] = b;
        return await PostJsonAsync("api/motion/axes", payload, ct);
    }

    /// <summary>
    /// Absolute Cartesian bulk LIN via <c>POST /api/motion/bulk_pose</c> (MS_CMD=99).
    /// Null components keep current <c>$POS_ACT</c>.
    /// </summary>
    public async Task<JsonDocument> MoveBulkPoseAsync(
        double? x = null, double? y = null, double? z = null,
        double? a = null, double? b = null, double? c = null,
        double? e1 = null, double speedMmS = 30, int? tool = null, int? baseIndex = null,
        double waitS = 120, double tolMm = 2, double tolDeg = 1.0,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["speed_mm_s"] = speedMmS,
            ["wait_s"] = waitS,
            ["tol_mm"] = tolMm,
            ["tol_deg"] = tolDeg,
        };
        if (x is double xv) payload["x"] = xv;
        if (y is double yv) payload["y"] = yv;
        if (z is double zv) payload["z"] = zv;
        if (a is double av) payload["a"] = av;
        if (b is double bv) payload["b"] = bv;
        if (c is double cv) payload["c"] = cv;
        if (e1 is double e1v) payload["e1"] = e1v;
        if (tool is int t) payload["tool"] = t;
        if (baseIndex is int bi) payload["base"] = bi;
        return await PostJsonAsync("api/motion/bulk_pose", payload, ct);
    }

    /// <summary>Relative bulk jump via <c>POST /api/motion/bulk</c> (MS_CMD=99 LIN).</summary>
    public async Task<JsonDocument> MoveBulkDeltaAsync(
        double dx = 0, double dy = 0, double dz = 0,
        double speedMmS = 30, double tolMm = 2, double waitS = 90,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["dx"] = dx,
            ["dy"] = dy,
            ["dz"] = dz,
            ["speed_mm_s"] = speedMmS,
            ["tol_mm"] = tolMm,
            ["wait_s"] = waitS,
        };
        return await PostJsonAsync("api/motion/bulk", payload, ct);
    }

    /// <summary>Controller <c>SPTP XHOME</c> via <c>POST /api/motion/home</c> (MS_CMD=94).</summary>
    public async Task<JsonDocument> GoHomeAsync(double waitS = 180, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?> { ["wait_s"] = waitS };
        return await PostJsonAsync("api/motion/home", payload, ct);
    }

    // ---- Waypoints / Movements (MassiveDRIVE is motion master) -----------------

    /// <summary>List taught waypoints (Cartesian poses + notes) from MassiveDRIVE.</summary>
    public Task<JsonDocument> ListWaypointsAsync(CancellationToken ct = default)
        => GetJsonAsync("api/waypoints", ct);

    /// <summary>List named Movements (sequences of waypoints) from MassiveDRIVE.</summary>
    public Task<JsonDocument> ListSequencesAsync(CancellationToken ct = default)
        => GetJsonAsync("api/sequences", ct);

    /// <summary>
    /// Start a Movement. Default <paramref name="async"/> so MassiveSLICER can poll
    /// <see cref="SequenceRunStatusAsync"/> and capture when waypoint notes request a scan.
    /// </summary>
    public Task<JsonDocument> StartSequenceAsync(
        string sequenceId, bool async = true, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?> { ["async"] = async };
        return PostJsonAsync($"api/sequences/{sequenceId}/run", payload, ct, longTimeout: true);
    }

    /// <summary>Live Movement run state: phase, pose, notes, capture_token.</summary>
    public Task<JsonDocument> SequenceRunStatusAsync(CancellationToken ct = default)
        => GetJsonAsync("api/sequences/run/status", ct);

    /// <summary>Ack a notes-triggered scan so MassiveDRIVE can leave the capture dwell.</summary>
    public Task<JsonDocument> SequenceCaptureAckAsync(
        string? captureToken, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["token"] = captureToken,
            ["capture_token"] = captureToken,
        };
        return PostJsonAsync("api/sequences/run/capture-ack", payload, ct, longTimeout: false);
    }

    /// <summary>Request stop of the active Movement after the current step.</summary>
    public Task<JsonDocument> SequenceRunStopAsync(CancellationToken ct = default)
        => PostJsonAsync("api/sequences/run/stop", new Dictionary<string, object?>(), ct, longTimeout: false);

    async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(new Uri(new Uri(BaseUrl), path), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new MassiveDriveClientException((int)resp.StatusCode, body);
        return JsonDocument.Parse(body);
    }

    async Task<JsonDocument> PostJsonAsync(
        string path, Dictionary<string, object?> payload, CancellationToken ct, bool longTimeout = true)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        // Bulk/axis moves can take up to wait_s — use a long client timeout.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (longTimeout && !cts.IsCancellationRequested)
            cts.CancelAfter(TimeSpan.FromMinutes(3));
        using var resp = await _http.PostAsync(new Uri(new Uri(BaseUrl), path), content, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new MassiveDriveClientException((int)resp.StatusCode, body);
        return JsonDocument.Parse(body);
    }

    async Task<JsonDocument> PostJsonAsync(string path, Dictionary<string, object?> payload, CancellationToken ct)
        => await PostJsonAsync(path, payload, ct, longTimeout: true);

    /// <summary>Upload package then start path executor (or dry-run).</summary>
    public async Task<MassiveDriveSendResult> SendAndStartAsync(
        Dictionary<string, object?> package,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        using var up = await UploadPackageAsync(package, ct);
        var packageId = up.RootElement.TryGetProperty("package_id", out var pid)
            ? pid.GetString()
            : null;
        if (string.IsNullOrEmpty(packageId))
            throw new MassiveDriveClientException(0, "upload did not return package_id: " + up.RootElement.GetRawText());

        var name = package.TryGetValue("name", out var n) ? n?.ToString() : packageId;
        using var start = await StartPackageAsync(packageId!, name, dryRun, ct);
        return new MassiveDriveSendResult(packageId!, up.RootElement.GetRawText(), start.RootElement.GetRawText());
    }

    public void Dispose()
    {
        if (_owns)
            _http.Dispose();
    }
}

public sealed record MassiveDriveSendResult(string PackageId, string UploadJson, string StartJson);

public sealed class MassiveDriveClientException : Exception
{
    public MassiveDriveClientException(int status, string body)
        : base($"MassiveDRIVE HTTP {status}: {body}")
    {
        StatusCode = status;
        Body = body;
    }

    public int StatusCode { get; }
    public string Body { get; }
}
