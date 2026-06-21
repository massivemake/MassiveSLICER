using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Repairs GLB files whose JSON buffer byteLength values exceed the embedded BIN chunk
/// (common in glTF-Transform v4 exports). Returns a temp path when repaired.
/// </summary>
internal static class GlbRepair
{
    private const uint GlbMagic = 0x46546C67; // glTF

    public static string EnsureLoadable(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return path; }

        if (!TryParse(bytes, out var jsonNode, out var bin, out _))
            return path;

        if (!NeedsRepair(jsonNode, bin.Length))
            return path;

        if (!TryRepair(jsonNode, bin.Length))
            return path;

        var repairedJson = jsonNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var jsonBytes    = Encoding.UTF8.GetBytes(repairedJson);
        var jsonPad      = (4 - jsonBytes.Length % 4) % 4;
        var jsonChunkLen = jsonBytes.Length + jsonPad;
        var binPad       = (4 - bin.Length % 4) % 4;
        var total        = 12 + 8 + jsonChunkLen + 8 + bin.Length + binPad;

        var outBytes = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(0), GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(8), (uint)total);

        var o = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o), (uint)jsonChunkLen);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o + 4), 0x4E4F534A); // JSON
        jsonBytes.CopyTo(outBytes.AsSpan(o + 8));
        if (jsonPad > 0)
            outBytes.AsSpan(o + 8 + jsonBytes.Length, jsonPad).Fill(0x20);
        o += 8 + jsonChunkLen;

        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o), (uint)bin.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o + 4), 0x004E4942); // BIN
        bin.CopyTo(outBytes.AsSpan(o + 8));

        var temp = Path.Combine(Path.GetTempPath(), "mslicer-glb-" + Guid.NewGuid().ToString("N") + ".glb");
        File.WriteAllBytes(temp, outBytes);
        return temp;
    }

    private static bool TryParse(byte[] bytes, out JsonNode json, out byte[] bin, out int jsonRawLen)
    {
        json = null!;
        bin  = Array.Empty<byte>();
        jsonRawLen = 0;

        if (bytes.Length < 20) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0)) != GlbMagic) return false;

        jsonRawLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        if (20 + jsonRawLen > bytes.Length) return false;

        var jsonText = Encoding.UTF8.GetString(bytes, 20, jsonRawLen);
        try { json = JsonNode.Parse(jsonText)!; }
        catch { return false; }

        var binStart = 20 + jsonRawLen;
        binStart += (4 - binStart % 4) % 4;
        if (binStart + 8 > bytes.Length) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(binStart + 4)) != 0x004E4942) return false;

        var binLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(binStart));
        binStart += 8;
        if (binStart + binLen > bytes.Length) return false;

        bin = bytes.AsSpan(binStart, binLen).ToArray();
        return true;
    }

    private static bool NeedsRepair(JsonNode json, int binLen)
    {
        if (json["buffers"] is not JsonArray buffers) return false;

        long cursor = 0;
        for (int i = 0; i < buffers.Count; i++)
        {
            if (buffers[i] is not JsonObject buf) continue;
            if (buf["uri"] is not null) continue;

            var declared = buf["byteLength"]?.GetValue<long>() ?? 0;
            var used     = UsedBytes(json, i);
            var need     = Math.Max(declared, used);

            if (cursor + need > binLen)
                return true;

            cursor += need;
        }

        return false;
    }

    private static bool TryRepair(JsonNode json, int binLen)
    {
        if (json["buffers"] is not JsonArray buffers) return false;

        for (int i = 0; i < buffers.Count; i++)
        {
            if (buffers[i] is not JsonObject buf) continue;
            if (buf["uri"] is not null) continue;

            var used = UsedBytes(json, i);
            if (used <= 0) continue;
            buf["byteLength"] = used;
        }

        // GLB embeds a single BIN chunk — collapse multiple embedded buffers into one.
        int embedded = 0;
        JsonObject? first = null;
        foreach (var b in buffers)
        {
            if (b is JsonObject o && o["uri"] is null)
            {
                embedded++;
                first ??= o;
            }
        }

        if (embedded > 1 && first is not null)
        {
            long offset = 0;
            var remap   = new Dictionary<int, long>();

            for (int i = 0; i < buffers.Count; i++)
            {
                if (buffers[i] is not JsonObject buf || buf["uri"] is not null) continue;
                remap[i] = offset;
                offset  += UsedBytes(json, i);
            }

            if (json["bufferViews"] is JsonArray views)
            {
                foreach (var v in views)
                {
                    if (v is not JsonObject view) continue;
                    if (view["buffer"]?.GetValue<int>() is not int bi) continue;
                    if (!remap.TryGetValue(bi, out var baseOff)) continue;
                    view["buffer"] = 0;
                    view["byteOffset"] = baseOff + (view["byteOffset"]?.GetValue<long>() ?? 0);
                }
            }

            while (buffers.Count > 1)
                buffers.RemoveAt(buffers.Count - 1);

            first["byteLength"] = binLen;
        }
        else if (embedded == 1 && first is not null)
        {
            first["byteLength"] = binLen;
        }

        return true;
    }

    private static long UsedBytes(JsonNode json, int bufferIndex)
    {
        long max = 0;
        if (json["bufferViews"] is not JsonArray views) return 0;

        foreach (var v in views)
        {
            if (v is not JsonObject view) continue;
            if (view["buffer"]?.GetValue<int>() != bufferIndex) continue;
            var end = (view["byteOffset"]?.GetValue<long>() ?? 0)
                    + (view["byteLength"]?.GetValue<long>() ?? 0);
            if (end > max) max = end;
        }

        return max;
    }
}