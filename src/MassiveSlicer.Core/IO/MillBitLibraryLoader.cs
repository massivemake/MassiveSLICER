using System.Text.Json;
using System.Text.Json.Serialization;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Persists the mill bit / cutting-tool library under AppData:
/// <c>%AppData%\MassiveSlicer\mill_tools.json</c>.
/// </summary>
public static class MillBitLibraryLoader
{
    /// <summary>Bumped when seed geometry / default spindle bit changes.</summary>
    public const int CurrentLibraryVersion = 3;

    public static readonly string LibraryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MassiveSlicer");

    public static readonly string LibraryPath = Path.Combine(LibraryDir, "mill_tools.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    sealed class LibraryFile
    {
        public int Version { get; set; }
        public List<MillBitTool> Tools { get; set; } = [];
    }

    public static List<MillBitTool> Load()
    {
        try
        {
            if (!File.Exists(LibraryPath))
            {
                var seed = MillBitTool.CreateSeedLibrary();
                Save(seed);
                return seed;
            }

            var json = File.ReadAllText(LibraryPath);
            List<MillBitTool>? tools = null;
            int version = 0;

            // Support both { Version, Tools } and legacy bare array.
            if (json.TrimStart().StartsWith('['))
            {
                tools = JsonSerializer.Deserialize<List<MillBitTool>>(json, Options);
            }
            else
            {
                var file = JsonSerializer.Deserialize<LibraryFile>(json, Options);
                tools = file?.Tools;
                version = file?.Version ?? 0;
            }

            if (tools is null || tools.Count == 0)
            {
                var seed = MillBitTool.CreateSeedLibrary();
                Save(seed);
                return seed;
            }

            NormalizeTools(tools);

            // Re-apply LFAM 3 mounted-bit defaults when the library is older than CurrentLibraryVersion.
            if (version < CurrentLibraryVersion)
            {
                UpsertDefaultSpindleBit(tools);
                Save(tools);
            }

            return tools;
        }
        catch
        {
            return MillBitTool.CreateSeedLibrary();
        }
    }

    /// <param name="touchTimestamps">
    /// When false, keep existing <see cref="MillBitTool.LastModifiedUtc"/> (ERP merge / ErpId stamp).
    /// </param>
    public static void Save(IEnumerable<MillBitTool> tools, bool touchTimestamps = true)
    {
        try
        {
            Directory.CreateDirectory(LibraryDir);
            var list = tools.ToList();
            NormalizeTools(list);
            if (touchTimestamps)
            {
                var now = DateTime.UtcNow;
                foreach (var t in list)
                    t.LastModifiedUtc = now;
            }

            var file = new LibraryFile
            {
                Version = CurrentLibraryVersion,
                Tools = list,
            };
            File.WriteAllText(LibraryPath, JsonSerializer.Serialize(file, Options));
        }
        catch
        {
            /* non-fatal */
        }
    }

    static void NormalizeTools(List<MillBitTool> tools)
    {
        foreach (var t in tools)
        {
            t.CuttingPresets ??= [];
            if (t.CuttingPresets.Count == 0)
                t.CuttingPresets.Add(new MillBitCuttingPreset());
            t.HolderSegments ??= [];
            if (t.ShaftDiameterMm <= 0)
                t.ShaftDiameterMm = t.DiameterMm;
            if (string.IsNullOrWhiteSpace(t.Identifier))
                t.Identifier = t.Name;
        }

        // Exactly one default spindle bit.
        if (!tools.Any(t => t.IsDefaultSpindleBit))
        {
            var flat = tools.FirstOrDefault(t =>
                t.Id == MillBitTool.DefaultSpindleBitId
                || t.Name.Contains("AP90", StringComparison.OrdinalIgnoreCase)
                || (t.Type == MillBitType.FlatEndMill && Math.Abs(t.DiameterMm - 76.2) < 0.05));
            if (flat is not null)
                flat.IsDefaultSpindleBit = true;
        }
    }

    static void UpsertDefaultSpindleBit(List<MillBitTool> tools)
    {
        var def = MillBitTool.CreateLfam3DefaultFlat3In();
        var idx = tools.FindIndex(t =>
            t.Id == MillBitTool.DefaultSpindleBitId
            || t.Name.Contains("AP90", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("D76.2", StringComparison.OrdinalIgnoreCase));

        foreach (var t in tools)
            t.IsDefaultSpindleBit = false;

        if (idx >= 0)
        {
            // Preserve id if they renamed, but overwrite shop geometry/cutting data from seed.
            def.Id = tools[idx].Id;
            tools[idx] = def;
        }
        else
        {
            tools.Insert(0, def);
        }
    }
}
