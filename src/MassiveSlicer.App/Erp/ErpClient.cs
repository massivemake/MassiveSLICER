using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MassiveSlicer.App.Erp;

/// <summary>
/// HTTP client for the ERP slicer API (<c>/api/slicer/v1/*</c>, bearer-token auth —
/// ERP issue #955). Phase 1 covers search + elements; slice upload and status
/// lifecycle endpoints grow here in later phases using the same
/// <see cref="ErpResult{T}"/> error channel.
///
/// Response parsing is hand-rolled over <see cref="JsonElement"/> and confined to
/// <see cref="ParseHit"/>/<see cref="ParseElement"/> with multi-name field lookups,
/// so ERP-side field drift only ever touches those two functions.
/// </summary>
public sealed class ErpClient : IDisposable
{
    private readonly HttpClient _http;

    public ErpClient(string baseUrl, string token)
    {
        var root = NormalizeBaseUrl(baseUrl);
        _http = new HttpClient
        {
            BaseAddress = new Uri(root),
            Timeout     = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    /// <summary>
    /// Users paste the ERP URL both bare (<c>https://erp.example.com</c>) and with the
    /// API path already on it (<c>https://erp.example.com/api/slicer/v1</c>). Requests
    /// always append <c>api/slicer/v1/…</c>, so strip a pasted suffix rather than 404
    /// on the doubled path.
    /// </summary>
    internal static string NormalizeBaseUrl(string baseUrl)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/api/slicer/v1", "/api/slicer" })
        {
            if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                root = root[..^suffix.Length];
                break;
            }
        }
        return root + "/";
    }

    /// <summary>Validates connectivity + token (empty search doubles as a health check
    /// until the ERP exposes a dedicated ping endpoint). The live ERP rejects queries
    /// under 2 characters with HTTP 400 — that still proves the endpoint is reachable
    /// and the token passed auth (bad tokens 401 first), so 400 counts as alive.</summary>
    public async Task<ErpResult<bool>> PingAsync(CancellationToken ct)
    {
        var r = await GetJsonAsync("api/slicer/v1/search?q=", ct);
        if (r.Error is not null &&
            !(r.Error.Kind == ErpErrorKind.BadResponse && r.Error.HttpStatus == 400))
            return ErpResult<bool>.Fail(r.Error);
        r.Value?.Dispose();
        return ErpResult<bool>.Success(true);
    }

    /// <summary>Combined Project/Lead quick-search by number (YY-NNN) and title.</summary>
    public async Task<ErpResult<IReadOnlyList<ErpSearchHit>>> SearchAsync(string query, CancellationToken ct)
    {
        var r = await GetJsonAsync($"api/slicer/v1/search?q={Uri.EscapeDataString(query)}", ct);
        if (r.Error is not null) return ErpResult<IReadOnlyList<ErpSearchHit>>.Fail(r.Error);

        using var doc = r.Value!;
        try
        {
            var hits = new List<ErpSearchHit>();
            foreach (var el in EnumerateArray(doc.RootElement, "projects", "leads", "results", "items", "data"))
                if (ParseHit(el) is { } hit)
                    hits.Add(hit);
            return ErpResult<IReadOnlyList<ErpSearchHit>>.Success(hits);
        }
        catch (Exception ex)
        {
            return ErpResult<IReadOnlyList<ErpSearchHit>>.Fail(ErpErrorKind.BadResponse, $"unexpected response: {ex.Message}");
        }
    }

    /// <summary>Lists a project's elements (id, name, element #, rev count).</summary>
    public async Task<ErpResult<IReadOnlyList<ErpElement>>> GetProjectElementsAsync(string projectId, CancellationToken ct)
    {
        var r = await GetJsonAsync($"api/slicer/v1/projects/{Uri.EscapeDataString(projectId)}/elements", ct);
        if (r.Error is not null) return ErpResult<IReadOnlyList<ErpElement>>.Fail(r.Error);

        using var doc = r.Value!;
        try
        {
            var elements = new List<ErpElement>();
            foreach (var el in EnumerateArray(doc.RootElement, "elements", "results", "items", "data"))
                if (ParseElement(el) is { } parsed)
                    elements.Add(parsed);
            return ErpResult<IReadOnlyList<ErpElement>>.Success(elements);
        }
        catch (Exception ex)
        {
            return ErpResult<IReadOnlyList<ErpElement>>.Fail(ErpErrorKind.BadResponse, $"unexpected response: {ex.Message}");
        }
    }

    /// <summary>Resolves a project by id via the elements endpoint's envelope
    /// (<c>{project: {id, projectNumber, name}, elements: [...]}</c>) — used to
    /// re-attach the workspace after a lead was converted to a project.</summary>
    public async Task<ErpResult<ErpProjectInfo>> GetProjectAsync(string projectId, CancellationToken ct)
    {
        var r = await GetJsonAsync($"api/slicer/v1/projects/{Uri.EscapeDataString(projectId)}/elements", ct);
        if (r.Error is not null) return ErpResult<ErpProjectInfo>.Fail(r.Error);

        using var doc = r.Value!;
        try
        {
            string number = "", title = "", id = projectId;
            if (TryGetPropertyCi(doc.RootElement, "project", out var proj) && proj.ValueKind == JsonValueKind.Object)
            {
                id     = GetString(proj, "id", "projectId") ?? projectId;
                number = GetString(proj, "projectNumber", "number", "no") ?? "";
                title  = GetString(proj, "name", "title") ?? "";
            }
            var elements = new List<ErpElement>();
            foreach (var el in EnumerateArray(doc.RootElement, "elements", "results", "items", "data"))
                if (ParseElement(el) is { } parsed)
                    elements.Add(parsed);
            return ErpResult<ErpProjectInfo>.Success(new ErpProjectInfo(id, number, title, elements));
        }
        catch (Exception ex)
        {
            return ErpResult<ErpProjectInfo>.Fail(ErpErrorKind.BadResponse, $"unexpected response: {ex.Message}");
        }
    }

    /// <summary>Creates a new element under a project or lead and returns it parsed.</summary>
    public async Task<ErpResult<ErpElement>> CreateElementAsync(
        string parentType, string parentId, string name, string? description, CancellationToken ct)
    {
        string collection = parentType.Equals("lead", StringComparison.OrdinalIgnoreCase) ? "leads" : "projects";
        var body = new Dictionary<string, object?> { ["name"] = name, ["description"] = description };
        var r = await PostJsonAsync($"api/slicer/v1/{collection}/{Uri.EscapeDataString(parentId)}/elements", body, ct);
        if (r.Error is not null) return ErpResult<ErpElement>.Fail(r.Error);

        using var doc = r.Value!;
        // Accept the element bare or wrapped in an "element" envelope.
        var root = doc.RootElement;
        if (TryGetPropertyCi(root, "element", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            root = wrapped;
        return ParseElement(root) is { } el
            ? ErpResult<ErpElement>.Success(el)
            : ErpResult<ErpElement>.Fail(ErpErrorKind.BadResponse, "created element missing from response");
    }

    /// <summary>Registers a slice (stats + UNAS file references) against an element.
    /// The ERP assigns and returns the rev number.</summary>
    public async Task<ErpResult<ErpSliceReceipt>> RegisterSliceAsync(
        string elementId, ErpSliceStats stats, IReadOnlyList<ErpSliceFile> files, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["stats"] = new Dictionary<string, object?>
            {
                ["printTime"]     = stats.PrintTime,
                ["weight"]        = stats.Weight,
                ["material"]      = stats.Material,
                ["layerHeightMm"] = stats.LayerHeightMm,
                ["beadWidthMm"]   = stats.BeadWidthMm,
            },
            ["files"] = files.Select(f => new Dictionary<string, object?>
            {
                ["kind"]  = f.Kind,
                ["path"]  = f.Path,
                ["bytes"] = f.Bytes,
            }).ToList(),
        };
        var r = await PostJsonAsync($"api/slicer/v1/elements/{Uri.EscapeDataString(elementId)}/slices", body, ct);
        if (r.Error is not null) return ErpResult<ErpSliceReceipt>.Fail(r.Error);

        using var doc = r.Value!;
        int rev = GetInt(doc.RootElement, "rev", "revision", "revNumber") ?? 0;
        return ErpResult<ErpSliceReceipt>.Success(
            new ErpSliceReceipt(rev, GetString(doc.RootElement, "url", "link")));
    }

    public void Dispose() => _http.Dispose();

    // -- Transport ---------------------------------------------------------

    private Task<ErpResult<JsonDocument>> GetJsonAsync(string relative, CancellationToken ct)
        => SendJsonAsync(() => _http.GetAsync(relative, ct), ct);

    private Task<ErpResult<JsonDocument>> PostJsonAsync(string relative, object body, CancellationToken ct)
        => SendJsonAsync(() =>
        {
            var content = new StringContent(
                JsonSerializer.Serialize(body, PostOptions),
                System.Text.Encoding.UTF8, "application/json");
            return _http.PostAsync(relative, content, ct);
        }, ct);

    private static readonly JsonSerializerOptions PostOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task<ErpResult<JsonDocument>> SendJsonAsync(
        Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using var resp = await send();
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ErpResult<JsonDocument>.Fail(ErpErrorKind.Unauthorized, "token invalid or revoked");
            if (!resp.IsSuccessStatusCode)
            {
                string body = "";
                try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* body optional */ }
                int status = (int)resp.StatusCode;
                return ErpResult<JsonDocument>.Fail(new ErpError(
                    ErpErrorKind.BadResponse, ExtractErrorMessage(body, status), status, body));
            }

            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return ErpResult<JsonDocument>.Success(doc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;                                             // caller-initiated (debounce) — let it unwind
        }
        catch (OperationCanceledException)
        {
            return ErpResult<JsonDocument>.Fail(ErpErrorKind.Timeout, "request timed out");
        }
        catch (HttpRequestException ex)
        {
            return ErpResult<JsonDocument>.Fail(ErpErrorKind.Network, ex.Message);
        }
        catch (JsonException ex)
        {
            return ErpResult<JsonDocument>.Fail(ErpErrorKind.BadResponse, $"invalid JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ErpResult<JsonDocument>.Fail(ErpErrorKind.Network, ex.Message);
        }
    }

    /// <summary>The ERP writes human-readable errors ("leads can't own elements —
    /// convert to a project first"); show those verbatim, falling back to the code.</summary>
    internal static string ExtractErrorMessage(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && GetString(doc.RootElement, "message", "error", "detail") is { Length: > 0 } m)
                return m;
        }
        catch { /* non-JSON body */ }
        return $"HTTP {status}";
    }

    /// <summary>Extracts the converted-project id from a lead element-create 400 body
    /// (ERP #961: the response includes the linked project's id so the slicer can
    /// offer re-attach). Null when the body carries no such hint.</summary>
    internal static string? ExtractLinkedProjectId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return GetString(doc.RootElement, "projectId", "convertedProjectId", "linkedProjectId");
        }
        catch { return null; }
    }

    // -- Parsing (the ONLY place ERP field names live) -----------------------

    /// <summary>
    /// Accepts a bare JSON array or an envelope object with the given list keys.
    /// All matching keys are concatenated — the live ERP splits search results into
    /// sibling <c>projects</c> and <c>leads</c> arrays in one envelope.
    /// </summary>
    internal static IEnumerable<JsonElement> EnumerateArray(JsonElement root, params string[] envelopeKeys)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray()) yield return el;
            yield break;
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in envelopeKeys)
                if (TryGetPropertyCi(root, key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var el in arr.EnumerateArray())
                        yield return el;
        }
    }

    internal static ErpSearchHit? ParseHit(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        string id = GetString(el, "id", "projectId", "leadId") ?? "";
        if (id.Length == 0) return null;

        var elements = new List<ErpElement>();
        if (TryGetPropertyCi(el, "elements", out var elArr) && elArr.ValueKind == JsonValueKind.Array)
            foreach (var e in elArr.EnumerateArray())
                if (ParseElement(e) is { } parsed)
                    elements.Add(parsed);

        return new ErpSearchHit(
            Type:     (GetString(el, "type", "kind") ?? "project").ToLowerInvariant(),
            Id:       id,
            Number:   GetString(el, "number", "no", "projectNumber", "leadNumber") ?? "",
            Title:    GetString(el, "title", "name") ?? "",
            Client:   GetString(el, "client", "clientName", "customer"),
            Elements: elements);
    }

    internal static ErpElement? ParseElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        string id = GetString(el, "id", "elementId") ?? "";
        if (id.Length == 0) return null;

        return new ErpElement(
            Id:            id,
            Name:          GetString(el, "name", "title", "elementName") ?? $"Element {id}",
            ElementNumber: GetString(el, "elementNumber", "element", "number", "no"),
            RevisionCount: GetInt(el, "revCount", "revisionCount", "revisions", "currentRevCount", "sliceCount") ?? 0);
    }

    /// <summary>Case-insensitive multi-name string lookup; numbers stringify.</summary>
    private static string? GetString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyCi(el, name, out var v)) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.String: return v.GetString();
                case JsonValueKind.Number: return v.GetRawText();
                case JsonValueKind.True:   return "true";
                case JsonValueKind.False:  return "false";
            }
        }
        return null;
    }

    private static int? GetInt(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyCi(el, name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int m)) return m;
        }
        return null;
    }

    private static bool TryGetPropertyCi(JsonElement el, string name, out JsonElement value)
    {
        if (el.TryGetProperty(name, out value)) return true;
        foreach (var prop in el.EnumerateObject())
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        value = default;
        return false;
    }
}
