using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.App.Erp;

/// <summary>
/// Lab is the source of truth for the team KRL Post-Processing default.
/// Pull on connect (404 = stay local). Publish is explicit — we never
/// overwrite Lab from a random shop PC on first connect.
/// </summary>
public static class ErpKrlPostProcessSync
{
    /// <summary>
    /// GET Lab default (or <c>presets-bundle.krlPostProcess</c>). Returns null settings
    /// when the route is not shipped yet (404).
    /// </summary>
    public static async Task<(string Summary, KrlPostProcessSettings? Settings)> PullAsync(
        ErpClient client, Action<string>? log, CancellationToken ct)
    {
        var direct = await client.GetKrlPostProcessAsync(ct);
        if (direct.Ok)
            return ApplyPulled(direct.Value!, log, "GET /krl-postprocess");

        if (direct.Error!.HttpStatus is 404)
        {
            var bundle = await client.GetPresetsBundleAsync(ct);
            if (bundle.Ok && bundle.Value!.KrlPostProcess is { } fromBundle)
                return ApplyPulled(fromBundle, log, "presets-bundle.krlPostProcess");

            log?.Invoke("[erp] KRL post-process API not on this ERP yet — local factory file only");
            return ("krl-postprocess API not available yet", null);
        }

        log?.Invoke($"[erp] krl-postprocess pull failed: {direct.Error.Kind} — {direct.Error.Message}");
        return ($"krl-postprocess pull failed: {direct.Error.Message}", null);
    }

    /// <summary>PUT current recipe as the Lab team default.</summary>
    public static async Task<string> PublishAsync(
        ErpClient client, KrlPostProcessSettings settings, Action<string>? log, CancellationToken ct)
    {
        settings.SchemaVersion = KrlPostProcessDocument.SchemaVersion;
        settings.UpdatedAtUtc = DateTime.UtcNow;
        var result = await client.PutKrlPostProcessAsync(settings, ct);
        if (!result.Ok)
        {
            if (result.Error!.HttpStatus is 404)
            {
                log?.Invoke("[erp] krl-postprocess PUT 404 — Lab has not shipped the route yet");
                return "Lab has no /krl-postprocess route yet";
            }
            log?.Invoke($"[erp] krl-postprocess publish failed: {result.Error.Message}");
            return $"publish failed: {result.Error.Message}";
        }

        KrlPostProcessLoader.Save(settings);
        var who = result.Value!.UpdatedBy ?? "Lab";
        var when = result.Value.UpdatedAt?.ToString("u") ?? settings.UpdatedAtUtc?.ToString("u") ?? "";
        var summary = $"published KRL post-process default to Lab ({who} {when})".Trim();
        log?.Invoke($"[erp] {summary}");
        return summary;
    }

    static (string, KrlPostProcessSettings?) ApplyPulled(
        ErpPresetEntry entry, Action<string>? log, string source)
    {
        if (!KrlPostProcessDocument.TryParse(entry.PayloadJson, out var settings, out var err))
        {
            log?.Invoke($"[erp] krl-postprocess payload invalid ({source}): {err}");
            return ($"invalid Lab payload: {err}", null);
        }

        if (entry.UpdatedAt is { } at)
            settings.UpdatedAtUtc = at.ToUniversalTime();
        KrlPostProcessLoader.Save(settings);
        var who = entry.UpdatedBy ?? "Lab";
        var when = settings.UpdatedAtUtc?.ToString("u") ?? "";
        var summary = $"pulled KRL post-process default from {source} ({who} {when})".Trim();
        log?.Invoke($"[erp] {summary}");
        return (summary, settings);
    }
}
