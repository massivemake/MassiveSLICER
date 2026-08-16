using System.Text.Json;
using System.Text.Json.Serialization;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.App.Erp;

/// <summary>
/// Pulls the shared print + material preset libraries from the ERP, merges into the
/// local AppData files, pushes any local-only rows, and can upsert a single row after
/// a desktop save. Safe when the ERP has not shipped the endpoints yet (404 → no-op).
/// </summary>
public static class ErpPresetSync
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented               = false,
    };

    /// <summary>
    /// On ERP connect: pull bundle → merge into local libraries → push local-only rows.
    /// Returns a short human summary for the console / status line.
    /// </summary>
    public static async Task<string> SyncOnConnectAsync(
        ErpClient client, Action<string>? log, CancellationToken ct)
    {
        var bundleResult = await client.GetPresetsBundleAsync(ct);
        if (!bundleResult.Ok)
        {
            // Prefer list endpoints if bundle is not deployed yet.
            if (bundleResult.Error!.HttpStatus is 404)
            {
                var printList = await client.ListPrintPresetsAsync(ct);
                var matList   = await client.ListMaterialPresetsAsync(ct);
                if (!printList.Ok && printList.Error!.HttpStatus is 404
                    && !matList.Ok && matList.Error!.HttpStatus is 404)
                {
                    log?.Invoke("[erp] presets API not available on this ERP yet — local AppData libraries only");
                    return "presets API not available yet";
                }

                var printEntries = printList.Ok ? printList.Value! : (IReadOnlyList<ErpPresetEntry>)[];
                var matEntries   = matList.Ok   ? matList.Value!   : (IReadOnlyList<ErpPresetEntry>)[];
                if (!printList.Ok && printList.Error!.HttpStatus is not 404)
                    log?.Invoke($"[erp] print-presets list failed: {printList.Error!.Message}");
                if (!matList.Ok && matList.Error!.HttpStatus is not 404)
                    log?.Invoke($"[erp] material-presets list failed: {matList.Error!.Message}");

                return await MergeAndPushAsync(client, printEntries, matEntries, log, ct);
            }

            log?.Invoke($"[erp] presets pull failed: {bundleResult.Error.Kind} — {bundleResult.Error.Message}");
            return $"presets pull failed: {bundleResult.Error.Message}";
        }

        var bundle = bundleResult.Value!;
        log?.Invoke($"[erp] presets-bundle v{Truncate(bundle.Version, 24)} — "
            + $"{bundle.PrintPresets.Count} print, {bundle.MaterialPresets.Count} material");
        return await MergeAndPushAsync(client, bundle.PrintPresets, bundle.MaterialPresets, log, ct);
    }

    private static async Task<string> MergeAndPushAsync(
        ErpClient client,
        IReadOnlyList<ErpPresetEntry> serverPrint,
        IReadOnlyList<ErpPresetEntry> serverMaterials,
        Action<string>? log,
        CancellationToken ct)
    {
        int printMerged = MergePrintFromServer(serverPrint);
        int matMerged   = MergeMaterialsFromServer(serverMaterials);

        int printPushed = await PushLocalOnlyPrintAsync(client, log, ct);
        int matPushed   = await PushLocalOnlyMaterialsAsync(client, log, ct);

        var summary =
            $"presets sync: +{printMerged} print from ERP, +{matMerged} materials from ERP; "
            + $"pushed {printPushed} print / {matPushed} material local-only";
        log?.Invoke($"[erp] {summary}");
        return summary;
    }

    /// <summary>After a local print-preset save — upsert one row to the ERP when connected.</summary>
    public static async Task PushPrintPresetAsync(
        ErpClient client, PrintPresetRecord record, Action<string>? log, CancellationToken ct)
    {
        // Do not send the local ErpId inside the payload (server owns id).
        var payload = ClonePrintForWire(record);
        var result = string.IsNullOrWhiteSpace(record.ErpId)
            ? await client.CreatePrintPresetAsync(payload, ct)
            : await client.UpdatePrintPresetAsync(record.ErpId!, payload, ct);

        if (!result.Ok)
        {
            if (result.Error!.HttpStatus is 404)
            {
                // Missing endpoint, or stale id — retry as create when we had an id.
                if (!string.IsNullOrWhiteSpace(record.ErpId))
                {
                    var create = await client.CreatePrintPresetAsync(payload, ct);
                    if (create.Ok)
                    {
                        StampPrintErpId(record.Name, create.Value!.Id);
                        log?.Invoke($"[erp] print preset \"{record.Name}\" re-created as {create.Value.Id}");
                        return;
                    }
                }
                log?.Invoke("[erp] print-preset push skipped — endpoint not available");
                return;
            }
            log?.Invoke($"[erp] print-preset push failed for \"{record.Name}\": {result.Error.Message}");
            return;
        }

        StampPrintErpId(record.Name, result.Value!.Id);
        log?.Invoke($"[erp] print preset \"{record.Name}\" → ERP id {result.Value.Id}");
    }

    /// <summary>After a local material-library save — upsert one material to the ERP.</summary>
    public static async Task PushMaterialPresetAsync(
        ErpClient client, MaterialPreset record, Action<string>? log, CancellationToken ct)
    {
        var payload = CloneMaterialForWire(record);
        var result = string.IsNullOrWhiteSpace(record.ErpId)
            ? await client.CreateMaterialPresetAsync(payload, ct)
            : await client.UpdateMaterialPresetAsync(record.ErpId!, payload, ct);

        if (!result.Ok)
        {
            if (result.Error!.HttpStatus is 404)
            {
                log?.Invoke("[erp] material-preset push skipped — endpoint not available");
                return;
            }
            if (!string.IsNullOrWhiteSpace(record.ErpId))
            {
                var create = await client.CreateMaterialPresetAsync(payload, ct);
                if (create.Ok)
                {
                    StampMaterialErpId(record.Name, create.Value!.Id);
                    log?.Invoke($"[erp] material \"{record.Name}\" re-created as {create.Value.Id}");
                    return;
                }
            }
            log?.Invoke($"[erp] material-preset push failed for \"{record.Name}\": {result.Error.Message}");
            return;
        }

        StampMaterialErpId(record.Name, result.Value!.Id);
        log?.Invoke($"[erp] material \"{record.Name}\" → ERP id {result.Value.Id}");
    }

    // -- Merge -----------------------------------------------------------------

    private static int MergePrintFromServer(IReadOnlyList<ErpPresetEntry> server)
    {
        var local = PrintPresetsLoader.Load();
        int addedOrUpdated = 0;

        foreach (var entry in server)
        {
            var remote = DeserializePrint(entry.PayloadJson);
            if (remote is null) continue;
            remote.ErpId = entry.Id;

            int idx = FindPrintIndex(local, entry.Id, remote.Name);
            if (idx < 0)
            {
                local.Add(remote);
                addedOrUpdated++;
            }
            else
            {
                // Server is team source of truth for shared library content.
                var keepFavorite = local[idx].IsFavorite;
                remote.IsFavorite = keepFavorite || remote.IsFavorite;
                if (!RecordsEqual(local[idx], remote))
                    addedOrUpdated++;
                local[idx] = remote;
            }
        }

        PrintPresetsLoader.Save(local);
        return addedOrUpdated;
    }

    private static int MergeMaterialsFromServer(IReadOnlyList<ErpPresetEntry> server)
    {
        var local = MaterialPresetsLoader.Load();
        int addedOrUpdated = 0;

        foreach (var entry in server)
        {
            var remote = DeserializeMaterial(entry.PayloadJson);
            if (remote is null) continue;
            remote.ErpId = entry.Id;

            int idx = FindMaterialIndex(local, entry.Id, remote.Name);
            if (idx < 0)
            {
                local.Add(remote);
                addedOrUpdated++;
            }
            else
            {
                if (!MaterialEqual(local[idx], remote))
                    addedOrUpdated++;
                local[idx] = remote;
            }
        }

        MaterialPresetsLoader.Save(local);
        return addedOrUpdated;
    }

    private static async Task<int> PushLocalOnlyPrintAsync(
        ErpClient client, Action<string>? log, CancellationToken ct)
    {
        var local = PrintPresetsLoader.Load();
        int pushed = 0;
        bool dirty = false;

        foreach (var row in local)
        {
            if (!string.IsNullOrWhiteSpace(row.ErpId)) continue;
            var payload = ClonePrintForWire(row);
            var result = await client.CreatePrintPresetAsync(payload, ct);
            if (!result.Ok)
            {
                if (result.Error!.HttpStatus is 404) return pushed; // stop hammering
                log?.Invoke($"[erp] could not upload print \"{row.Name}\": {result.Error.Message}");
                continue;
            }
            row.ErpId = result.Value!.Id;
            dirty = true;
            pushed++;
        }

        if (dirty) PrintPresetsLoader.Save(local);
        return pushed;
    }

    private static async Task<int> PushLocalOnlyMaterialsAsync(
        ErpClient client, Action<string>? log, CancellationToken ct)
    {
        var local = MaterialPresetsLoader.Load();
        int pushed = 0;
        bool dirty = false;

        foreach (var row in local)
        {
            if (!string.IsNullOrWhiteSpace(row.ErpId)) continue;
            var payload = CloneMaterialForWire(row);
            var result = await client.CreateMaterialPresetAsync(payload, ct);
            if (!result.Ok)
            {
                if (result.Error!.HttpStatus is 404) return pushed;
                log?.Invoke($"[erp] could not upload material \"{row.Name}\": {result.Error.Message}");
                continue;
            }
            row.ErpId = result.Value!.Id;
            dirty = true;
            pushed++;
        }

        if (dirty) MaterialPresetsLoader.Save(local);
        return pushed;
    }

    // -- Helpers ---------------------------------------------------------------

    private static void StampPrintErpId(string name, string id)
    {
        var local = PrintPresetsLoader.Load();
        int idx = FindPrintIndex(local, id, name);
        if (idx < 0) return;
        local[idx].ErpId = id;
        PrintPresetsLoader.Save(local);
    }

    private static void StampMaterialErpId(string name, string id)
    {
        var local = MaterialPresetsLoader.Load();
        int idx = FindMaterialIndex(local, id, name);
        if (idx < 0)
        {
            // Name match only
            idx = local.FindIndex(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        if (idx < 0) return;
        local[idx].ErpId = id;
        MaterialPresetsLoader.Save(local);
    }

    private static int FindPrintIndex(List<PrintPresetRecord> local, string? erpId, string name)
    {
        if (!string.IsNullOrWhiteSpace(erpId))
        {
            int byId = local.FindIndex(p => string.Equals(p.ErpId, erpId, StringComparison.Ordinal));
            if (byId >= 0) return byId;
        }
        return local.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static int FindMaterialIndex(List<MaterialPreset> local, string? erpId, string name)
    {
        if (!string.IsNullOrWhiteSpace(erpId))
        {
            int byId = local.FindIndex(p => string.Equals(p.ErpId, erpId, StringComparison.Ordinal));
            if (byId >= 0) return byId;
        }
        return local.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static PrintPresetRecord? DeserializePrint(string json)
    {
        try { return JsonSerializer.Deserialize<PrintPresetRecord>(json, JsonOptions); }
        catch { return null; }
    }

    private static MaterialPreset? DeserializeMaterial(string json)
    {
        try { return JsonSerializer.Deserialize<MaterialPreset>(json, JsonOptions); }
        catch { return null; }
    }

    /// <summary>Wire payload without local ErpId (server assigns id).</summary>
    private static PrintPresetRecord ClonePrintForWire(PrintPresetRecord r)
    {
        var json = JsonSerializer.Serialize(r, JsonOptions);
        var clone = JsonSerializer.Deserialize<PrintPresetRecord>(json, JsonOptions) ?? r;
        clone.ErpId = null;
        return clone;
    }

    private static MaterialPreset CloneMaterialForWire(MaterialPreset m)
    {
        var json = JsonSerializer.Serialize(m, JsonOptions);
        var clone = JsonSerializer.Deserialize<MaterialPreset>(json, JsonOptions) ?? m;
        clone.ErpId = null;
        return clone;
    }

    private static bool RecordsEqual(PrintPresetRecord a, PrintPresetRecord b)
        => JsonSerializer.Serialize(ClonePrintForWire(a), JsonOptions)
        == JsonSerializer.Serialize(ClonePrintForWire(b), JsonOptions);

    private static bool MaterialEqual(MaterialPreset a, MaterialPreset b)
        => JsonSerializer.Serialize(CloneMaterialForWire(a), JsonOptions)
        == JsonSerializer.Serialize(CloneMaterialForWire(b), JsonOptions);

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n];
}
