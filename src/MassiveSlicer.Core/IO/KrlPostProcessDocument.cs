using System.Text.Json;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Import/export envelope for the full KRL Post-Processing recipe
/// (Rules + Header + Footer). Same JSON Lab stores as
/// <c>payload</c> on <c>GET/PUT /api/slicer/v1/krl-postprocess</c>.
/// </summary>
public static class KrlPostProcessDocument
{
    public const string Kind = "MassiveSLICER.KrlPostProcess";
    public const int SchemaVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>
    /// Accepts the envelope, a bare <see cref="KrlPostProcessSettings"/> object
    /// (repo <c>assets/krl_postprocess.json</c>), or <c>{ payload: {…} }</c>.
    /// </summary>
    public static bool TryParse(string json, out KrlPostProcessSettings settings, out string? error)
    {
        settings = new KrlPostProcessSettings();
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "file is empty";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "JSON root must be an object";
                return false;
            }

            if (TryGet(root, "payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                settings = payload.Deserialize<KrlPostProcessSettings>(JsonOptions)
                           ?? new KrlPostProcessSettings();
            }
            else
            {
                settings = root.Deserialize<KrlPostProcessSettings>(JsonOptions)
                           ?? new KrlPostProcessSettings();
            }

            StampIfMissing(settings, root);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static KrlPostProcessSettings Parse(string json)
        => TryParse(json, out var s, out var err)
            ? s
            : throw new InvalidDataException(err ?? "invalid KRL post-process JSON");

    /// <summary>Envelope written by Export. Lab PUT body is <c>{ payload }</c> of the inner object.</summary>
    public static string SerializeEnvelope(KrlPostProcessSettings settings, DateTime? updatedAtUtc = null)
    {
        settings.SchemaVersion = SchemaVersion;
        settings.UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow;
        var envelope = new Dictionary<string, object?>
        {
            ["kind"] = Kind,
            ["schemaVersion"] = SchemaVersion,
            ["updatedAt"] = settings.UpdatedAtUtc.Value.ToString("o"),
            ["payload"] = settings,
        };
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static string SerializePayload(KrlPostProcessSettings settings)
    {
        settings.SchemaVersion = SchemaVersion;
        settings.UpdatedAtUtc ??= DateTime.UtcNow;
        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    static void StampIfMissing(KrlPostProcessSettings settings, JsonElement root)
    {
        if (settings.SchemaVersion <= 0)
            settings.SchemaVersion = SchemaVersion;
        if (settings.UpdatedAtUtc is null
            && TryGet(root, "updatedAt", out var at)
            && at.ValueKind == JsonValueKind.String
            && DateTime.TryParse(at.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            settings.UpdatedAtUtc = dt.ToUniversalTime();
    }

    static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
