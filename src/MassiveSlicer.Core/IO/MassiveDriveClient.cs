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
            _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
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
