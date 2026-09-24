using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.App.Erp;

/// <summary>
/// Lab is the source of truth for the team KRL Post-Processing recipes.
/// Pull on connect (404 = stay local). Publish is explicit — we never
/// overwrite Lab from a random shop PC on first connect.
/// </summary>
/// <remarks>
/// The Lab stores one document: a shared recipe plus one complete recipe per robot
/// (<see cref="KrlPostProcessCells"/>). Publishing replaces only the active robot's entry,
/// on top of what the Lab holds at that moment, so a PC with a stale copy of another
/// robot can't roll that robot back.
/// </remarks>
public static class ErpKrlPostProcessSync
{
    /// <summary>
    /// GET Lab document (or <c>presets-bundle.krlPostProcess</c>). Returns null settings
    /// when the route is not shipped yet (404). <c>MissingCells</c> lists robots this PC
    /// had an entry for that the Lab no longer has (see <see cref="KrlPostProcessSectionBackup"/>).
    /// </summary>
    public static async Task<(string Summary, KrlPostProcessSettings? Settings, IReadOnlyList<string> MissingCells)> PullAsync(
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
            return ("krl-postprocess API not available yet", null, []);
        }

        log?.Invoke($"[erp] krl-postprocess pull failed: {direct.Error.Kind} — {direct.Error.Message}");
        return ($"krl-postprocess pull failed: {direct.Error.Message}", null, []);
    }

    /// <summary>
    /// Publish <paramref name="recipe"/> as <paramref name="cellName"/>'s recipe. Reads the
    /// Lab's current document first and replaces only that robot's entry.
    /// </summary>
    public static async Task<string> PublishCellAsync(
        ErpClient client, string cellName, KrlPostProcessSettings recipe,
        Action<string>? log, CancellationToken ct)
    {
        var (current, error) = await ReadLabDocumentAsync(client, log, ct);
        if (current is null) return error!;
        // A publish is also a look at the Lab: notice robots it lost since the last pull
        // before this write moves the "last seen" list past them.
        KrlPostProcessSectionBackup.Reconcile(current, KrlPostProcessLoader.Load());

        recipe.UpdatedAtUtc = DateTime.UtcNow;
        var doc = KrlPostProcessCells.WithCell(current, cellName, recipe);
        var summary = await PublishAsync(client, doc, log, ct);
        if (!summary.StartsWith("published", StringComparison.Ordinal)) return summary;
        KrlPostProcessSectionBackup.Published(doc);
        return $"{summary} — {cellName} only";
    }

    /// <summary>
    /// Re-publish the robot recipes this PC parked when they vanished from the Lab. Robots
    /// the Lab has an entry for again are left alone.
    /// </summary>
    public static async Task<string> RestoreMissingCellsAsync(
        ErpClient client, Action<string>? log, CancellationToken ct)
    {
        var parked = KrlPostProcessSectionBackup.Load();
        if (parked.Count == 0) return "no parked robot recipes on this PC";

        var (current, error) = await ReadLabDocumentAsync(client, log, ct);
        if (current is null) return error!;
        // A publish is also a look at the Lab: notice robots it lost since the last pull
        // before this write moves the "last seen" list past them.
        KrlPostProcessSectionBackup.Reconcile(current, KrlPostProcessLoader.Load());

        var doc = KrlPostProcessCells.RestoreMissing(
            current, KrlPostProcessSectionBackup.Load(), out var restored);
        if (restored.Count == 0)
        {
            KrlPostProcessSectionBackup.Published(current);
            return "the Lab already has every parked robot recipe — nothing restored";
        }

        var summary = await PublishAsync(client, doc, log, ct);
        if (!summary.StartsWith("published", StringComparison.Ordinal)) return summary;
        KrlPostProcessSectionBackup.Published(doc);
        var result = $"restored {string.Join(", ", restored)} to Lab from this PC";
        log?.Invoke($"[erp] {result}");
        return result;
    }

    /// <summary>PUT the whole document as the Lab recipe and save it locally.</summary>
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
        var summary = $"published KRL post-process to Lab ({who} {when})".Trim();
        log?.Invoke($"[erp] {summary}");
        return summary;
    }

    /// <summary>
    /// The Lab's current document to publish on top of. 404 (no recipe route or nothing
    /// published yet) falls back to this PC's file. Any other failure returns null plus the
    /// reason: publishing blind could wipe other robots' entries.
    /// </summary>
    static async Task<(KrlPostProcessSettings? Doc, string? Error)> ReadLabDocumentAsync(
        ErpClient client, Action<string>? log, CancellationToken ct)
    {
        var direct = await client.GetKrlPostProcessAsync(ct);
        if (direct.Ok)
        {
            if (KrlPostProcessDocument.TryParse(direct.Value!.PayloadJson, out var doc, out var err))
                return (doc, null);
            log?.Invoke($"[erp] krl-postprocess payload invalid, not publishing: {err}");
            return (null, $"not published — the Lab's current recipe could not be read ({err})");
        }
        if (direct.Error!.HttpStatus is 404)
            return (KrlPostProcessLoader.Load(), null);

        log?.Invoke($"[erp] krl-postprocess read before publish failed: {direct.Error.Message}");
        return (null, $"not published — could not read the Lab's current recipe ({direct.Error.Message})");
    }

    static (string, KrlPostProcessSettings?, IReadOnlyList<string>) ApplyPulled(
        ErpPresetEntry entry, Action<string>? log, string source)
    {
        if (!KrlPostProcessDocument.TryParse(entry.PayloadJson, out var settings, out var err))
        {
            log?.Invoke($"[erp] krl-postprocess payload invalid ({source}): {err}");
            return ($"invalid Lab payload: {err}", null, []);
        }

        if (entry.UpdatedAt is { } at)
            settings.UpdatedAtUtc = at.ToUniversalTime();

        // Park robot entries the Lab lost BEFORE the pulled document replaces the local file.
        var missing = KrlPostProcessSectionBackup.Reconcile(settings, KrlPostProcessLoader.Load());
        KrlPostProcessLoader.Save(settings);

        var who = entry.UpdatedBy ?? "Lab";
        var when = settings.UpdatedAtUtc?.ToString("u") ?? "";
        var summary = $"pulled KRL post-process from {source} ({who} {when})".Trim();
        log?.Invoke($"[erp] {summary}");
        if (missing.Count > 0)
            log?.Invoke($"[erp] ⚠ Lab KRL recipe has no entry for {string.Join(", ", missing)} — " +
                        "probably published from an older slicer build. Those robots export the shared " +
                        "recipe until restored (KRL Post-Processing → Restore from this PC).");
        return (summary, settings, missing);
    }
}
