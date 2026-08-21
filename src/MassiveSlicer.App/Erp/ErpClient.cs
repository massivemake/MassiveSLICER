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
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    /// <summary>
    /// Password login — no bearer yet. POST /api/slicer/v1/login.
    /// Lab must ship this route (see docs/ERP-Login-API-Replit-Prompt.md).
    /// </summary>
    public static async Task<ErpResult<ErpLoginResult>> LoginAsync(
        string baseUrl, string email, string password, CancellationToken ct)
    {
        using var client = new ErpClient(baseUrl, token: "");
        var body = new Dictionary<string, object?>
        {
            ["email"]    = email.Trim(),
            ["username"] = email.Trim(),
            ["password"] = password,
        };
        var r = await client.PostJsonAsync("api/slicer/v1/login", body, ct);
        if (r.Error is not null) return ErpResult<ErpLoginResult>.Fail(r.Error);
        using var doc = r.Value!;
        return ParseLogin(doc.RootElement) is { } parsed
            ? ErpResult<ErpLoginResult>.Success(parsed)
            : ErpResult<ErpLoginResult>.Fail(ErpErrorKind.BadResponse, "login response missing token");
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
    /// The ERP assigns and returns the rev number. <paramref name="extra"/> merges
    /// additional top-level fields into the payload (e.g. a <c>sentToRobot</c> block
    /// when the program was pushed to the controller).</summary>
    public async Task<ErpResult<ErpSliceReceipt>> RegisterSliceAsync(
        string elementId, ErpSliceStats stats, IReadOnlyList<ErpSliceFile> files, CancellationToken ct,
        IReadOnlyDictionary<string, object?>? extra = null)
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
                ["printTimeSec"]  = stats.PrintTimeSec,
                ["weightKg"]      = stats.WeightKg,
            },
            ["files"] = files.Select(f => new Dictionary<string, object?>
            {
                ["kind"]  = f.Kind,
                ["path"]  = f.Path,
                ["bytes"] = f.Bytes,
            }).ToList(),
        };
        if (extra is not null)
            foreach (var (key, value) in extra)
                body[key] = value;
        var r = await PostJsonAsync($"api/slicer/v1/elements/{Uri.EscapeDataString(elementId)}/slices", body, ct);
        if (r.Error is not null) return ErpResult<ErpSliceReceipt>.Fail(r.Error);

        using var doc = r.Value!;
        int rev = GetInt(doc.RootElement, "rev", "revision", "revNumber") ?? 0;
        ErpCosting? costing = null;
        if (TryGetPropertyCi(doc.RootElement, "costing", out var costEl) && costEl.ValueKind == JsonValueKind.Object)
            costing = ParseCosting(costEl);
        return ErpResult<ErpSliceReceipt>.Success(
            new ErpSliceReceipt(rev, GetString(doc.RootElement, "url", "link"), costing));
    }

    /// <summary>
    /// Tells MassiveLAB where a <c>.mass</c> just landed. Metadata only (no bytes).
    /// Lab may 404 until the endpoint ships — the slicer still keeps the JSONL log.
    /// </summary>
    public async Task<ErpResult<bool>> NotifyWorkspaceSavedAsync(
        MassiveSlicer.Core.IO.WorkspaceSaveRecord rec, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["at"]            = rec.At,
            ["path"]          = rec.UnasPath ?? rec.Path,
            ["localPath"]     = rec.Path,
            ["bytes"]         = rec.Bytes,
            ["file"]          = rec.File,
            ["cell"]          = rec.Cell,
            ["host"]          = rec.Host,
            ["projectType"]   = rec.ProjectType,
            ["projectId"]     = rec.ProjectId,
            ["projectNumber"] = rec.ProjectNumber,
            ["projectTitle"]  = rec.ProjectTitle,
            ["elementId"]     = rec.ElementId,
            ["elementName"]   = rec.ElementName,
        };
        var r = await PostJsonAsync("api/slicer/v1/workspace-saves", body, ct);
        if (r.Error is not null) return ErpResult<bool>.Fail(r.Error);
        return ErpResult<bool>.Success(true);
    }

    /// <summary>Fetches the ERP's pricing configuration (rates, materials catalog,
    /// markup, quantity discounts). Cache by <see cref="ErpPricingConfig.Version"/> and
    /// re-fetch when a quote/costing echoes a different version.</summary>
    public async Task<ErpResult<ErpPricingConfig>> GetPricingAsync(CancellationToken ct)
    {
        var r = await GetJsonAsync("api/slicer/v1/pricing", ct);
        if (r.Error is not null) return ErpResult<ErpPricingConfig>.Fail(r.Error);

        using var doc = r.Value!;
        try
        {
            return ParsePricing(doc.RootElement) is { } cfg
                ? ErpResult<ErpPricingConfig>.Success(cfg)
                : ErpResult<ErpPricingConfig>.Fail(ErpErrorKind.BadResponse, "pricing config missing from response");
        }
        catch (Exception ex)
        {
            return ErpResult<ErpPricingConfig>.Fail(ErpErrorKind.BadResponse, $"unexpected response: {ex.Message}");
        }
    }

    /// <summary>Requests an authoritative quote for the given slice stats. The ERP
    /// computes the exact breakdown it would use itself — do no pricing math locally
    /// beyond rough live estimates from the cached config.</summary>
    public async Task<ErpResult<ErpCosting>> GetQuoteAsync(ErpQuoteRequest req, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["printTimeSec"] = req.PrintTimeSec,
            ["weightKg"]     = req.WeightKg,
            ["material"]     = req.Material,
            ["quantity"]     = req.Quantity,
            ["finishing"]    = req.Finishing,
            ["customMachineRatePerHour"] = req.CustomMachineRatePerHour,
        };
        var r = await PostJsonAsync("api/slicer/v1/quote", body, ct);
        if (r.Error is not null) return ErpResult<ErpCosting>.Fail(r.Error);

        using var doc = r.Value!;
        try
        {
            var root = doc.RootElement;
            if (TryGetPropertyCi(root, "quote", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
                root = wrapped;
            return ErpResult<ErpCosting>.Success(ParseCosting(root));
        }
        catch (Exception ex)
        {
            return ErpResult<ErpCosting>.Fail(ErpErrorKind.BadResponse, $"unexpected response: {ex.Message}");
        }
    }

    // -- Shared print / material preset library --------------------------------

    /// <summary>GET /presets-bundle — both libraries in one round-trip.</summary>
    public async Task<ErpResult<ErpPresetsBundle>> GetPresetsBundleAsync(CancellationToken ct)
    {
        var r = await GetJsonAsync("api/slicer/v1/presets-bundle", ct);
        if (r.Error is not null) return ErpResult<ErpPresetsBundle>.Fail(r.Error);
        using var doc = r.Value!;
        try
        {
            return ErpResult<ErpPresetsBundle>.Success(ParsePresetsBundle(doc.RootElement));
        }
        catch (Exception ex)
        {
            return ErpResult<ErpPresetsBundle>.Fail(ErpErrorKind.BadResponse, $"unexpected response: {ex.Message}");
        }
    }

    public Task<ErpResult<IReadOnlyList<ErpPresetEntry>>> ListPrintPresetsAsync(CancellationToken ct)
        => ListPresetCollectionAsync("api/slicer/v1/print-presets", ct);

    public Task<ErpResult<IReadOnlyList<ErpPresetEntry>>> ListMaterialPresetsAsync(CancellationToken ct)
        => ListPresetCollectionAsync("api/slicer/v1/material-presets", ct);

    public Task<ErpResult<ErpPresetEntry>> CreatePrintPresetAsync(object payload, CancellationToken ct)
        => CreatePresetAsync("api/slicer/v1/print-presets", payload, ct);

    public Task<ErpResult<ErpPresetEntry>> UpdatePrintPresetAsync(string id, object payload, CancellationToken ct)
        => UpdatePresetAsync($"api/slicer/v1/print-presets/{Uri.EscapeDataString(id)}", payload, ct);

    public Task<ErpResult<bool>> DeletePrintPresetAsync(string id, CancellationToken ct)
        => DeleteJsonAsync($"api/slicer/v1/print-presets/{Uri.EscapeDataString(id)}", ct);

    public Task<ErpResult<ErpPresetEntry>> CreateMaterialPresetAsync(object payload, CancellationToken ct)
        => CreatePresetAsync("api/slicer/v1/material-presets", payload, ct);

    public Task<ErpResult<ErpPresetEntry>> UpdateMaterialPresetAsync(string id, object payload, CancellationToken ct)
        => UpdatePresetAsync($"api/slicer/v1/material-presets/{Uri.EscapeDataString(id)}", payload, ct);

    public Task<ErpResult<bool>> DeleteMaterialPresetAsync(string id, CancellationToken ct)
        => DeleteJsonAsync($"api/slicer/v1/material-presets/{Uri.EscapeDataString(id)}", ct);

    private async Task<ErpResult<IReadOnlyList<ErpPresetEntry>>> ListPresetCollectionAsync(
        string relative, CancellationToken ct)
    {
        var r = await GetJsonAsync(relative, ct);
        if (r.Error is not null) return ErpResult<IReadOnlyList<ErpPresetEntry>>.Fail(r.Error);
        using var doc = r.Value!;
        try
        {
            var items = new List<ErpPresetEntry>();
            foreach (var el in EnumerateArray(doc.RootElement, "items", "printPresets", "materialPresets",
                         "presets", "results", "data"))
                if (ParsePresetEntry(el) is { } entry)
                    items.Add(entry);
            // Bare array already handled by EnumerateArray.
            return ErpResult<IReadOnlyList<ErpPresetEntry>>.Success(items);
        }
        catch (Exception ex)
        {
            return ErpResult<IReadOnlyList<ErpPresetEntry>>.Fail(
                ErpErrorKind.BadResponse, $"unexpected response: {ex.Message}");
        }
    }

    private async Task<ErpResult<ErpPresetEntry>> CreatePresetAsync(
        string relative, object payload, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["payload"] = payload };
        var r = await PostJsonAsync(relative, body, ct);
        if (r.Error is not null) return ErpResult<ErpPresetEntry>.Fail(r.Error);
        using var doc = r.Value!;
        return ParsePresetEntry(doc.RootElement) is { } entry
            ? ErpResult<ErpPresetEntry>.Success(entry)
            : ErpResult<ErpPresetEntry>.Fail(ErpErrorKind.BadResponse, "preset missing from create response");
    }

    private async Task<ErpResult<ErpPresetEntry>> UpdatePresetAsync(
        string relative, object payload, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["payload"] = payload };
        var r = await PutJsonAsync(relative, body, ct);
        if (r.Error is not null) return ErpResult<ErpPresetEntry>.Fail(r.Error);
        if (r.Value is null)
            return ErpResult<ErpPresetEntry>.Fail(ErpErrorKind.BadResponse, "empty update response");
        using var doc = r.Value;
        return ParsePresetEntry(doc.RootElement) is { } entry
            ? ErpResult<ErpPresetEntry>.Success(entry)
            : ErpResult<ErpPresetEntry>.Fail(ErpErrorKind.BadResponse, "preset missing from update response");
    }

    public void Dispose() => _http.Dispose();

    // -- Transport ---------------------------------------------------------

    private Task<ErpResult<JsonDocument>> GetJsonAsync(string relative, CancellationToken ct)
        => SendJsonAsync(() => _http.GetAsync(relative, ct), ct, allowEmpty: false);

    private Task<ErpResult<JsonDocument>> PostJsonAsync(string relative, object body, CancellationToken ct)
        => SendJsonAsync(() =>
        {
            var content = new StringContent(
                JsonSerializer.Serialize(body, PostOptions),
                System.Text.Encoding.UTF8, "application/json");
            return _http.PostAsync(relative, content, ct);
        }, ct, allowEmpty: false);

    private Task<ErpResult<JsonDocument>> PutJsonAsync(string relative, object body, CancellationToken ct)
        => SendJsonAsync(() =>
        {
            var content = new StringContent(
                JsonSerializer.Serialize(body, PostOptions),
                System.Text.Encoding.UTF8, "application/json");
            return _http.PutAsync(relative, content, ct);
        }, ct, allowEmpty: true);

    private async Task<ErpResult<bool>> DeleteJsonAsync(string relative, CancellationToken ct)
    {
        var r = await SendJsonAsync(() => _http.DeleteAsync(relative, ct), ct, allowEmpty: true);
        if (r.Error is not null) return ErpResult<bool>.Fail(r.Error);
        r.Value?.Dispose();
        return ErpResult<bool>.Success(true);
    }

    private static readonly JsonSerializerOptions PostOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="allowEmpty">
    /// When true, HTTP 204 / empty 2xx bodies succeed with a null document
    /// (DELETE and some PUT implementations).
    /// </param>
    private static async Task<ErpResult<JsonDocument>> SendJsonAsync(
        Func<Task<HttpResponseMessage>> send, CancellationToken ct, bool allowEmpty)
    {
        try
        {
            using var resp = await send();
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                string body401 = "";
                try { body401 = await resp.Content.ReadAsStringAsync(ct); } catch { /* optional */ }
                string msg = ExtractErrorMessage(body401, (int)resp.StatusCode);
                if (string.IsNullOrWhiteSpace(msg) || msg.StartsWith("HTTP ", StringComparison.Ordinal))
                    msg = "token invalid or revoked";
                return ErpResult<JsonDocument>.Fail(new ErpError(ErpErrorKind.Unauthorized, msg, (int)resp.StatusCode, body401));
            }
            if (!resp.IsSuccessStatusCode)
            {
                string body = "";
                try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* body optional */ }
                int status = (int)resp.StatusCode;
                return ErpResult<JsonDocument>.Fail(new ErpError(
                    ErpErrorKind.BadResponse, ExtractErrorMessage(body, status), status, body));
            }

            if (resp.StatusCode == HttpStatusCode.NoContent)
                return allowEmpty
                    ? ErpResult<JsonDocument>.Success(null!)
                    : ErpResult<JsonDocument>.Fail(ErpErrorKind.BadResponse, "empty response");

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
                return allowEmpty
                    ? ErpResult<JsonDocument>.Success(null!)
                    : ErpResult<JsonDocument>.Fail(ErpErrorKind.BadResponse, "empty response");

            var doc = JsonDocument.Parse(bytes);
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

    internal static ErpPricingConfig? ParsePricing(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (TryGetPropertyCi(root, "pricing", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            root = wrapped;

        // Machine rates arrive flat or under a machineRate/machineRates/rates object
        // (the live ERP uses singular "machineRate").
        var rates = root;
        foreach (var key in new[] { "machineRate", "machineRates", "rates" })
            if (TryGetPropertyCi(root, key, out var mr) && mr.ValueKind == JsonValueKind.Object)
            {
                rates = mr;
                break;
            }

        double? overhead = GetDouble(root, "overheadRate", "overhead");
        double? profit   = GetDouble(root, "profitRate", "profit");
        if (TryGetPropertyCi(root, "markup", out var mu) && mu.ValueKind == JsonValueKind.Object)
        {
            overhead ??= GetDouble(mu, "overheadRate", "overhead");
            profit   ??= GetDouble(mu, "profitRate", "profit");
        }

        var materials = new List<ErpPricingMaterial>();
        foreach (var el in EnumerateArray(
                     TryGetPropertyCi(root, "materials", out var mats) ? mats : default,
                     "materials", "items"))
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            string name = GetString(el, "name", "material", "title") ?? "";
            if (name.Length == 0) continue;
            materials.Add(new ErpPricingMaterial(
                Id:          GetString(el, "id", "materialId") ?? name,
                Name:        name,
                Type:        GetString(el, "type", "materialType"),
                CostPerKg:   GetDouble(el, "costPerKg", "pricePerKg"),
                CostPerLb:   GetDouble(el, "costPerLb", "pricePerLb"),
                DensityGmCc: GetDouble(el, "density", "densityGCm3", "densityGmCc")));
        }

        var discounts = new List<ErpQuantityDiscount>();
        if (TryGetPropertyCi(root, "quantityDiscounts", out var qd) && qd.ValueKind == JsonValueKind.Array)
            foreach (var el in qd.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                int? min = GetInt(el, "minQuantity", "minQty", "min", "quantity");
                double? rate = GetDouble(el, "rate", "discount", "discountRate", "discountPercent", "percent");
                if (min is { } m && rate is { } dr)
                    discounts.Add(new ErpQuantityDiscount(m, dr > 1 ? dr / 100.0 : dr));
            }

        return new ErpPricingConfig(
            Version:                  GetString(root, "version", "pricingVersion", "hash") ?? "",
            RatePerHour:              GetDouble(rates, "effectiveRatePerHour", "ratePerHour", "machineRatePerHour"),
            RateWithFinishingPerHour: GetDouble(rates, "effectiveRateWithFinishingPerHour", "rateWithFinishingPerHour"),
            OverheadRate:             overhead is { } o && o > 1 ? o / 100.0 : overhead,
            ProfitRate:               profit   is { } pr && pr > 1 ? pr / 100.0 : profit,
            Materials:                materials,
            QuantityDiscounts:        discounts.OrderBy(d => d.MinQuantity).ToList());
    }

    internal static ErpPresetsBundle ParsePresetsBundle(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new ErpPresetsBundle("", [], []);

        string version = GetString(root, "version", "etag", "hash", "updatedAt") ?? "";
        var print = new List<ErpPresetEntry>();
        var mats  = new List<ErpPresetEntry>();

        if (TryGetPropertyCi(root, "printPresets", out var printArr) && printArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in printArr.EnumerateArray())
                if (ParsePresetEntry(item) is { } parsed) print.Add(parsed);
        }
        else if (TryGetPropertyCi(root, "print", out var printAlt) && printAlt.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in printAlt.EnumerateArray())
                if (ParsePresetEntry(item) is { } parsed) print.Add(parsed);
        }

        if (TryGetPropertyCi(root, "materialPresets", out var matArr) && matArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in matArr.EnumerateArray())
                if (ParsePresetEntry(item) is { } parsed) mats.Add(parsed);
        }
        else if (TryGetPropertyCi(root, "materials", out var matAlt) && matAlt.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in matAlt.EnumerateArray())
                if (ParsePresetEntry(item) is { } parsed) mats.Add(parsed);
        }

        return new ErpPresetsBundle(version, print, mats);
    }

    /// <summary>
    /// Accepts <c>{ id, updatedAt, updatedBy?, payload: {...} }</c> or a bare payload object
    /// with an optional id field (tolerant of ERP shape drift).
    /// </summary>
    internal static ErpPresetEntry? ParsePresetEntry(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        string id = GetString(el, "id", "presetId", "uuid") ?? "";
        DateTime? updatedAt = null;
        if (GetString(el, "updatedAt", "updated", "modifiedAt") is { Length: > 0 } u
            && DateTime.TryParse(u, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            updatedAt = dt.ToUniversalTime();

        string? updatedBy = GetString(el, "updatedBy", "user", "author");

        JsonElement payloadEl = el;
        if (TryGetPropertyCi(el, "payload", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            payloadEl = wrapped;

        // Bare payloads need an id either at top level or we cannot update later.
        if (id.Length == 0)
            id = GetString(payloadEl, "id", "ErpId", "erpId") ?? "";

        // Synthesize a stable-enough id from Name when the server returns bare rows without id
        // (should be rare — creates still need a real server id).
        if (id.Length == 0)
        {
            var name = GetString(payloadEl, "Name", "name") ?? "";
            if (name.Length == 0) return null;
            id = "name:" + name;
        }

        string payloadJson = payloadEl.GetRawText();
        return new ErpPresetEntry(id, updatedAt, updatedBy, payloadJson);
    }

    internal static ErpCosting ParseCosting(JsonElement el)
    {
        // Live-ERP shape: per-unit costs nest under "perUnit"; quantityDiscount and
        // markup are objects ({rate, amount} / {overheadAmount, profitAmount,
        // totalAmount}). Flat scalar variants are accepted too.
        var perUnit = el;
        if (TryGetPropertyCi(el, "perUnit", out var pu) && pu.ValueKind == JsonValueKind.Object)
            perUnit = pu;

        double? discount = GetDouble(el, "quantityDiscount", "discount", "discountAmount");
        if (discount is null && TryGetPropertyCi(el, "quantityDiscount", out var qd) && qd.ValueKind == JsonValueKind.Object)
            discount = GetDouble(qd, "amount");

        double? markup = GetDouble(el, "markup", "markupAmount");
        if (markup is null && TryGetPropertyCi(el, "markup", out var mk) && mk.ValueKind == JsonValueKind.Object)
            markup = GetDouble(mk, "totalAmount", "amount");

        return new(
            MachineCost:      GetDouble(perUnit, "machineCost", "machine") ?? GetDouble(el, "machineCost"),
            MaterialCost:     GetDouble(perUnit, "materialCost", "material") ?? GetDouble(el, "materialCost"),
            QuantityDiscount: discount,
            Markup:           markup,
            SubtotalCost:     GetDouble(el, "subtotalCost", "subtotal"),
            ClientPrice:      GetDouble(el, "clientPrice", "price", "total", "clientTotal"),
            PricingVersion:   GetString(el, "pricingVersion", "version"));
    }

    private static double? GetDouble(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!TryGetPropertyCi(el, name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
            if (v.ValueKind == JsonValueKind.String
                && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out double p)) return p;
        }
        return null;
    }

    internal static ErpLoginResult? ParseLogin(JsonElement root)
    {
        var el = root;
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (TryGetPropertyCi(el, "token", out _) is false
            && TryGetPropertyCi(el, "data", out var data) && data.ValueKind == JsonValueKind.Object)
            el = data;
        if (TryGetPropertyCi(el, "token", out _) is false
            && TryGetPropertyCi(el, "slicer", out var slicer) && slicer.ValueKind == JsonValueKind.Object)
            el = slicer;

        string token = GetString(el, "token", "accessToken", "access_token", "apiToken", "api_token") ?? "";
        if (token.Length == 0 && TryGetPropertyCi(root, "slicerToken", out var st) && st.ValueKind == JsonValueKind.String)
            token = st.GetString() ?? "";
        if (token.Length == 0) return null;

        DateTime? expires = null;
        if (GetString(el, "expiresAt", "expires_at", "expiry", "exp") is { Length: > 0 } exp
            && DateTime.TryParse(exp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            expires = dt.ToUniversalTime();

        string? email = GetString(el, "email", "userEmail")
                        ?? GetString(root, "email");
        string? name  = GetString(el, "name", "displayName", "fullName")
                        ?? GetString(root, "name", "displayName");
        if (name is null && TryGetPropertyCi(root, "user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            email ??= GetString(user, "email");
            name  = GetString(user, "name", "displayName", "fullName");
        }

        return new ErpLoginResult(token, email, name, expires);
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
