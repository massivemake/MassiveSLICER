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
/// Windows: DPAPI LocalMachine blob in ProgramData, so every login can read it.
/// macOS: <c>~/.config/MassiveSlicer/drive-upload.json</c>.
/// Not stored in the repo or the cell JSON.
/// </summary>
public static class MassiveDriveUploadToken
{
    public const string TokenHint =
        "Drive upload token is not on this PC. An admin runs scripts\\Install-DriveUploadToken.ps1 once. "
        + "Users do not type a password.";

    public static bool PreferUpload(bool endpoint, bool authInstalled, string? token)
        => endpoint && authInstalled && !string.IsNullOrWhiteSpace(token);

    public static string? ForCell(string? cellId, string? filePath = null)
    {
        var env = Environment.GetEnvironmentVariable("MASSIVESLICER_DRIVE_TOKEN");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

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
