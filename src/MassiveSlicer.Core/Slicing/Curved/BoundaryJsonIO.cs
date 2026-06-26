using System.Text.Json;

namespace MassiveSlicer.Core.Slicing.Curved;

/// <summary>Load/save compas_slicer-compatible boundary vertex index JSON.</summary>
public static class BoundaryJsonIO
{
    public static IReadOnlyList<int> LoadIndices(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
            return ParseIndexArray(root);

        if (root.TryGetProperty("vertices", out var verts))
            return ParseIndexArray(verts);

        if (root.TryGetProperty("low", out var low))
            return ParseIndexArray(low);

        throw new InvalidDataException($"Unrecognized boundary JSON format: {path}");
    }

    public static (IReadOnlyList<int> low, IReadOnlyList<int> high) LoadPair(string lowPath, string highPath) =>
        (LoadIndices(lowPath), LoadIndices(highPath));

    public static (IReadOnlyList<int> low, IReadOnlyList<int> high) LoadCombined(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("low", out var low) || !root.TryGetProperty("high", out var high))
            throw new InvalidDataException($"Combined boundary JSON must contain 'low' and 'high': {path}");
        return (ParseIndexArray(low), ParseIndexArray(high));
    }

    public static void SaveIndices(IReadOnlyList<int> indices, string path)
    {
        var json = JsonSerializer.Serialize(indices, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public static void SavePair(IReadOnlyList<int> low, IReadOnlyList<int> high, string lowPath, string highPath)
    {
        SaveIndices(low, lowPath);
        SaveIndices(high, highPath);
    }

    private static IReadOnlyList<int> ParseIndexArray(JsonElement el)
    {
        var list = new List<int>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number)
                list.Add(item.GetInt32());
        }
        return list;
    }
}