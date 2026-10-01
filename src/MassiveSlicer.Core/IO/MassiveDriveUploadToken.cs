using System.Text;
using System.Text.Json;

namespace MassiveSlicer.Core.IO;

/// <summary>What <c>GET /api/health</c> says about the HTTP job intake.</summary>
public readonly record struct MassiveDriveUploadCaps(bool Endpoint, bool AuthInstalled)
{
    public static MassiveDriveUploadCaps FromHealth(JsonElement root)
    {
        static bool Flag(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        return new MassiveDriveUploadCaps(Flag(root, "upload"), Flag(root, "upload_auth"));
    }
}

/// <summary>
/// Shop-PC token for <c>POST /api/jobs/package/upload</c>.
/// Priority order: (1) Lab login response <c>driveTokens</c> cached in memory,
/// (2) env var <c>MASSIVESLICER_DRIVE_TOKEN</c>,
/// (3) <c>%ProgramData%\MassiveSlicer\drive-upload.bin</c> (DPAPI LocalMachine, Windows only),
/// (4) <c>~/.config/MassiveSlicer/drive-upload.json</c> (macOS / fallback JSON).
/// Not stored in the repo or the cell JSON.
/// </summary>
public static class MassiveDriveUploadToken
{
    /// <summary>
    /// Tokens received from Lab login (<c>driveTokens</c> in the 200 response).
    /// Set by <see cref="CacheFromLogin"/> on Connect; cleared on disconnect.
    /// Keyed by lower-case cell id (e.g. "lfam1").
    /// </summary>
    static IReadOnlyDictionary<string, string>? _loginCache;

    /// <summary>
    /// Called by ErpViewModel after a successful login. Caches the Drive tokens
    /// for the lifetime of this session — no file write needed.
    /// </summary>
    public static void CacheFromLogin(IReadOnlyDictionary<string, string>? tokens)
        => _loginCache = tokens is { Count: > 0 } ? tokens : null;

    /// <summary>Clear cached tokens on ERP disconnect.</summary>
    public static void ClearLoginCache() => _loginCache = null;
    public const string TokenHint =
        "Drive upload token not available. Log in to MassiveLAB in Slicer (email + password) — "
        + "Drive tokens arrive automatically. If Lab login is not available, an admin runs "
        + "scripts\\Install-DriveUploadToken.ps1 once per shop PC.";

    /// <summary>
    /// Returns true when Send should use HTTP upload.
    /// Drive must have the endpoint. The token must be non-empty — it can come from
    /// a Lab login (no installer required) or an on-disk store.
    /// <paramref name="authInstalled"/> (Drive health <c>upload_auth</c>) is still
    /// checked so we don't send to a Drive that hasn't been restarted with a token file yet.
    /// </summary>
    public static bool PreferUpload(bool endpoint, bool authInstalled, string? token)
        => endpoint && authInstalled && !string.IsNullOrWhiteSpace(token);

    public static string? ForCell(string? cellId, string? filePath = null)
    {
        // 1. Lab login response (driveTokens) — no file or installer needed
        if (_loginCache is not null)
        {
            string key = (cellId ?? "").Trim().ToLowerInvariant();
            if (key.Length > 0 && _loginCache.TryGetValue(key, out var cached) && !string.IsNullOrWhiteSpace(cached))
                return cached;
        }

        // 2. Env override
        var env = Environment.GetEnvironmentVariable("MASSIVESLICER_DRIVE_TOKEN");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        // 3. On-disk store (DPAPI on Windows, JSON elsewhere)
        foreach (var path in CandidatePaths(filePath))
        {
            if (!File.Exists(path))
                continue;
            var text = ReadStore(path);
            var token = TokenForCell(text, cellId);
            if (!string.IsNullOrWhiteSpace(token))
                return token;
        }
        return null;
    }

    public static IEnumerable<string> CandidatePaths(string? filePath = null)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            yield return filePath;
            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MassiveSlicer");
            yield return Path.Combine(dir, "drive-upload.bin");
            yield return Path.Combine(dir, "drive-upload.json");
            yield break;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".config", "MassiveSlicer", "drive-upload.json");
    }

    internal static string? TokenForCell(string? text, string? cellId)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (!text.StartsWith('{') && !text.StartsWith('['))
        {
            var line = text.Split('\n', 2)[0].Trim();
            return string.IsNullOrEmpty(line) || line.StartsWith('#') ? null : line;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            string key = (cellId ?? "").Trim();
            if (key.Length > 0 && TryGet(doc.RootElement, key, out var specific))
                return specific;
            if (TryGet(doc.RootElement, "default", out var fallback))
                return fallback;
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    static bool TryGet(JsonElement obj, string name, out string? value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = prop.Value.GetString()?.Trim();
            return !string.IsNullOrEmpty(value);
        }
        value = null;
        return false;
    }

    static string? ReadStore(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        if (bytes.Length == 0)
            return null;
        if (bytes[0] == (byte)'{' || bytes[0] == (byte)'[')
            return Encoding.UTF8.GetString(bytes);
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return Encoding.UTF8.GetString(DpapiLocalMachine.Unprotect(bytes));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
