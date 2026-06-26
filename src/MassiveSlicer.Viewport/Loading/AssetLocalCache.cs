using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Copies UNC/network GLBs to <c>%LOCALAPPDATA%\MassiveSlicer\asset-cache</c> so parsing
/// does not re-read multi-megabyte files over SMB on every cell load.
/// JSON glTF files disguised as <c>.glb</c> with external <c>.bin</c> sidecars are embedded
/// into a single binary GLB in the cache.
/// </summary>
internal static class AssetLocalCache
{
    private const uint GlbMagic = 0x46546C67; // glTF

    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MassiveSlicer", "asset-cache");

    public static string EnsureLocal(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        if (!full.StartsWith(@"\\", StringComparison.Ordinal))
            return full;

        try
        {
            var hash   = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..20];
            var cached = Path.Combine(CacheRoot, hash + Path.GetExtension(full));
            var srcUtc = File.GetLastWriteTimeUtc(full);

            if (File.Exists(cached))
            {
                var dstUtc = File.GetLastWriteTimeUtc(cached);
                if (dstUtc >= srcUtc)
                {
                    var cachedBytes = File.ReadAllBytes(cached);
                    if (IsBinaryGlb(cachedBytes))
                        return cached;
                }
            }

            Directory.CreateDirectory(CacheRoot);
            var bytes = File.ReadAllBytes(full);
            if (TryEmbedJsonGltf(full, bytes, out var embedded))
                bytes = embedded;

            File.WriteAllBytes(cached, bytes);
            File.SetLastWriteTimeUtc(cached, srcUtc);
            return cached;
        }
        catch
        {
            return full;
        }
    }

    private static bool TryEmbedJsonGltf(string sourcePath, byte[] bytes, out byte[] embeddedGlb)
    {
        embeddedGlb = Array.Empty<byte>();
        if (!LooksLikeJsonGltf(bytes))
            return false;

        JsonNode json;
        try { json = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!; }
        catch { return false; }

        if (json["buffers"] is not JsonArray buffers || buffers.Count == 0)
            return false;

        var sourceDir = Path.GetDirectoryName(sourcePath) ?? "";
        var binChunks = new List<byte[]>(buffers.Count);
        bool hasExternal = false;

        foreach (var entry in buffers)
        {
            if (entry is not JsonObject buf)
            {
                binChunks.Add(Array.Empty<byte>());
                continue;
            }

            if (buf["uri"]?.GetValue<string>() is { } uri && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                hasExternal = true;
                var sidecar = Path.Combine(sourceDir, uri);
                if (!File.Exists(sidecar))
                    throw new FileNotFoundException($"Missing glTF sidecar '{uri}' for '{sourcePath}'.", sidecar);

                binChunks.Add(File.ReadAllBytes(sidecar));
                buf.Remove("uri");
            }
            else if (buf["uri"]?.GetValue<string>() is { } dataUri
                  && dataUri.StartsWith("data:application/octet-stream;base64,", StringComparison.OrdinalIgnoreCase))
            {
                hasExternal = true;
                var b64 = dataUri["data:application/octet-stream;base64,".Length..];
                binChunks.Add(Convert.FromBase64String(b64));
                buf.Remove("uri");
            }
            else
            {
                binChunks.Add(Array.Empty<byte>());
            }
        }

        if (!hasExternal)
            return false;

        var merged = MergeBufferChunks(json, buffers, binChunks);
        foreach (var buf in buffers)
        {
            if (buf is JsonObject o)
                o["byteLength"] = merged.Length;
        }

        embeddedGlb = WriteGlb(json, merged);
        return true;
    }

    private static byte[] MergeBufferChunks(JsonNode json, JsonArray buffers, IReadOnlyList<byte[]> chunks)
    {
        if (buffers.Count == 1)
            return chunks[0];

        long total = 0;
        var remap  = new Dictionary<int, long>();

        for (int i = 0; i < buffers.Count; i++)
        {
            remap[i] = total;
            total   += chunks[i].Length;
        }

        if (json["bufferViews"] is JsonArray views)
        {
            foreach (var v in views)
            {
                if (v is not JsonObject view) continue;
                if (view["buffer"]?.GetValue<int>() is not int bi) continue;
                if (!remap.TryGetValue(bi, out var baseOff)) continue;
                view["buffer"]     = 0;
                view["byteOffset"] = baseOff + (view["byteOffset"]?.GetValue<long>() ?? 0);
            }
        }

        while (buffers.Count > 1)
            buffers.RemoveAt(buffers.Count - 1);

        var merged = new byte[total];
        long cursor = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(merged.AsSpan((int)cursor));
            cursor += chunk.Length;
        }

        return merged;
    }

    private static byte[] WriteGlb(JsonNode json, byte[] bin)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var jsonPad   = (4 - jsonBytes.Length % 4) % 4;
        var jsonChunk = jsonBytes.Length + jsonPad;
        var total     = 12 + 8 + jsonChunk + 8 + bin.Length + ((4 - bin.Length % 4) % 4);

        var outBytes = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(0), GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(8), (uint)total);

        var o = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o), (uint)jsonChunk);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o + 4), 0x4E4F534A);
        jsonBytes.CopyTo(outBytes.AsSpan(o + 8));
        if (jsonPad > 0)
            outBytes.AsSpan(o + 8 + jsonBytes.Length, jsonPad).Fill(0x20);
        o += 8 + jsonChunk;

        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o), (uint)bin.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(o + 4), 0x004E4942);
        bin.CopyTo(outBytes.AsSpan(o + 8));
        return outBytes;
    }

    private static bool IsBinaryGlb(byte[] bytes)
        => bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0)) == GlbMagic;

    private static bool LooksLikeJsonGltf(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            var c = (char)bytes[i];
            if (char.IsWhiteSpace(c)) continue;
            return c == '{';
        }

        return false;
    }
}