using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Append-only JSONL log of every successful <c>.mass</c> save so MassiveLAB
/// (and the shop) can see where the file landed. Written to AppData always,
/// and to <c>{UnasProjectsRoot}/_slicer/workspace-saves.jsonl</c> when the share is up.
/// </summary>
public static class WorkspaceSaveLog
{
    public const string FileName = "workspace-saves.jsonl";

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
    };

    /// <summary>AppData copy — always attempted.</summary>
    public static string LocalLogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MassiveSlicer", FileName);

    /// <summary>UNAS copy Lab can ingest: <c>Projects/_slicer/workspace-saves.jsonl</c>.</summary>
    public static string? ShareLogPath(string? unasProjectsRoot)
    {
        if (string.IsNullOrWhiteSpace(unasProjectsRoot)) return null;
        return Path.Combine(unasProjectsRoot, "_slicer", FileName);
    }

    public static WorkspaceSaveRecord Build(
        string path,
        string? unasProjectsRoot,
        string? cellName,
        ErpAttachment? attachment)
    {
        long bytes = 0;
        try { if (File.Exists(path)) bytes = new FileInfo(path).Length; } catch { /* ignore */ }

        return new WorkspaceSaveRecord
        {
            At             = DateTime.UtcNow.ToString("o"),
            Path           = path,
            UnasPath       = UnasPaths.ToShareRelative(path, unasProjectsRoot),
            Bytes          = bytes,
            File           = System.IO.Path.GetFileName(path),
            Cell           = string.IsNullOrWhiteSpace(cellName) ? null : cellName,
            Host           = Environment.MachineName,
            ProjectType    = attachment?.Type,
            ProjectId      = attachment?.Id,
            ProjectNumber  = attachment?.Number,
            ProjectTitle   = attachment?.Title,
            ElementId      = attachment?.ElementId,
            ElementName    = attachment?.ElementName,
        };
    }

    /// <summary>Appends one record. Failures are returned, never thrown.</summary>
    public static IReadOnlyList<string> Append(WorkspaceSaveRecord record, string? unasProjectsRoot)
    {
        var line = JsonSerializer.Serialize(record, JsonOpts) + "\n";
        var written = new List<string>(2);
        TryWrite(LocalLogPath, line, written);
        if (ShareLogPath(unasProjectsRoot) is { } share)
            TryWrite(share, line, written);
        return written;
    }

    /// <summary>Newest-first unique paths from the local + share JSONL logs.</summary>
    public static IReadOnlyList<WorkspaceSaveRecord> ReadRecent(int limit, string? unasProjectsRoot)
    {
        var raw = new List<WorkspaceSaveRecord>();
        ReadFile(LocalLogPath, raw);
        if (ShareLogPath(unasProjectsRoot) is { } share)
            ReadFile(share, raw);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<WorkspaceSaveRecord>();
        for (int i = raw.Count - 1; i >= 0 && outList.Count < Math.Max(1, limit); i--)
        {
            var rec = raw[i];
            var key = rec.UnasPath ?? rec.Path;
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;
            outList.Add(rec);
        }
        return outList;
    }

    static void ReadFile(string dest, List<WorkspaceSaveRecord> into)
    {
        try
        {
            if (!File.Exists(dest)) return;
            foreach (var line in File.ReadLines(dest))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<WorkspaceSaveRecord>(line, JsonOpts);
                    if (rec is not null) into.Add(rec);
                }
                catch { /* skip bad line */ }
            }
        }
        catch { /* share offline */ }
    }

    static void TryWrite(string dest, string line, List<string> written)
    {
        try
        {
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(dest, line, Encoding.UTF8);
            written.Add(dest);
        }
        catch
        {
            // Share offline or AppData locked — the other destination may still land.
        }
    }
}

public sealed class WorkspaceSaveRecord
{
    public string  At            { get; set; } = "";
    public string  Path          { get; set; } = "";
    public string? UnasPath      { get; set; }
    public long    Bytes         { get; set; }
    public string  File          { get; set; } = "";
    public string? Cell          { get; set; }
    public string? Host          { get; set; }
    public string? ProjectType   { get; set; }
    public string? ProjectId     { get; set; }
    public string? ProjectNumber { get; set; }
    public string? ProjectTitle  { get; set; }
    public string? ElementId     { get; set; }
    public string? ElementName   { get; set; }
}
