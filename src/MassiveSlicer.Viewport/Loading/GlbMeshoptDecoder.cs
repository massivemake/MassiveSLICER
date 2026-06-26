using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Decompresses EXT_meshopt_compression GLBs at load time (like MassiveCONNECT's MeshoptDecoder).
/// Results are cached under <c>%LOCALAPPDATA%\MassiveSlicer\meshopt-cache</c>.
/// </summary>
internal static class GlbMeshoptDecoder
{
    private const uint GlbMagic = 0x46546C67; // glTF
    private const string Extension = "EXT_meshopt_compression";

    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MassiveSlicer", "meshopt-cache");

    private static readonly object CacheLock = new();

    public static string EnsureDecoded(string path)
    {
        var full = Path.GetFullPath(path);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch { return full; }

        if (!TryParse(bytes, out var json, out var bin, out _) || !UsesMeshopt(json))
            return full;

        var cached = CachePath(full);
        if (TryUseCache(full, cached))
            return cached;

        lock (CacheLock)
        {
            if (TryUseCache(full, cached))
                return cached;

            var tmp = cached + ".tmp";
            try
            {
                Directory.CreateDirectory(CacheRoot);
                var (outJson, outBin) = Decode(json, bin);
                WriteGlb(tmp, outJson, outBin);
                if (File.Exists(cached))
                    File.Delete(cached);
                File.Move(tmp, cached);
                File.SetLastWriteTimeUtc(cached, File.GetLastWriteTimeUtc(full));
                return cached;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
                try { if (File.Exists(cached)) File.Delete(cached); } catch { /* best effort */ }
                throw;
            }
        }
    }

    private static bool TryUseCache(string sourcePath, string cached)
    {
        if (!File.Exists(cached))
            return false;

        var srcUtc = File.GetLastWriteTimeUtc(sourcePath);
        var dstUtc = File.GetLastWriteTimeUtc(cached);
        return dstUtc >= srcUtc;
    }

    private static string CachePath(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        var key  = $"{fullPath.ToLowerInvariant()}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(CacheRoot, hash + ".glb");
    }

    private static bool UsesMeshopt(JsonNode json)
    {
        if (json["extensionsRequired"] is JsonArray req)
        {
            foreach (var e in req)
                if (e?.GetValue<string>() == Extension) return true;
        }
        if (json["extensionsUsed"] is JsonArray used)
        {
            foreach (var e in used)
                if (e?.GetValue<string>() == Extension) return true;
        }
        return false;
    }

    private static (JsonNode Json, byte[] Bin) Decode(JsonNode json, byte[] compressedBin)
    {
        if (json["bufferViews"] is not JsonArray views)
            throw new InvalidDataException("meshopt GLB has no bufferViews.");

        long outLen = 0;
        foreach (var v in views)
        {
            if (v is not JsonObject view) continue;
            var end = (view["byteOffset"]?.GetValue<long>() ?? 0)
                    + (view["byteLength"]?.GetValue<long>() ?? 0);
            if (end > outLen) outLen = end;
        }

        if (outLen <= 0)
            throw new InvalidDataException("meshopt GLB has empty output buffer.");

        var outBin = new byte[outLen];

        foreach (var v in views)
        {
            if (v is not JsonObject view) continue;
            if (view["extensions"]?["EXT_meshopt_compression"] is not JsonObject ext)
                throw new InvalidDataException("meshopt GLB bufferView missing compression extension.");

            long compOffset = ext["byteOffset"]?.GetValue<long>() ?? 0;
            long compLength = ext["byteLength"]?.GetValue<long>() ?? 0;
            long stride     = ext["byteStride"]?.GetValue<long>() ?? 0;
            long count      = ext["count"]?.GetValue<long>() ?? 0;
            var mode        = ext["mode"]?.GetValue<string>() ?? "";
            var filter      = ext["filter"]?.GetValue<string>() ?? "NONE";

            long viewOffset = view["byteOffset"]?.GetValue<long>() ?? 0;
            long viewLength = view["byteLength"]?.GetValue<long>() ?? 0;

            if (compOffset + compLength > compressedBin.Length)
                throw new InvalidDataException("meshopt compressed range exceeds BIN chunk.");

            var slice = compressedBin.AsSpan((int)compOffset, (int)compLength);
            DecodeView(slice, outBin.AsSpan((int)viewOffset, (int)viewLength), mode, filter, count, stride);
        }

        StripMeshoptExtensions(json);
        if (json["buffers"] is JsonArray buffers && buffers.Count > 0 && buffers[0] is JsonObject first)
            first["byteLength"] = outLen;

        return (json, outBin);
    }

    private static void DecodeView(
        ReadOnlySpan<byte> compressed, Span<byte> destination,
        string mode, string filter, long count, long stride)
    {
        if (count <= 0 || stride <= 0 || destination.Length != count * stride)
            throw new InvalidDataException($"meshopt view size mismatch ({mode}, count={count}, stride={stride}).");

        unsafe
        {
            fixed (byte* dst = destination)
            fixed (byte* src = compressed)
            {
                int rc = mode switch
                {
                    "ATTRIBUTES" => MeshoptNative.meshopt_decodeVertexBuffer(
                        (IntPtr)dst, (nuint)count, (nuint)stride, (IntPtr)src, (nuint)compressed.Length),
                    "TRIANGLES" => MeshoptNative.meshopt_decodeIndexBuffer(
                        (IntPtr)dst, (nuint)count, (nuint)stride, (IntPtr)src, (nuint)compressed.Length),
                    "INDICES" => MeshoptNative.meshopt_decodeIndexSequence(
                        (IntPtr)dst, (nuint)count, (nuint)stride, (IntPtr)src, (nuint)compressed.Length),
                    _ => throw new NotSupportedException($"Unsupported meshopt mode: {mode}"),
                };

                if (rc != 0)
                    throw new InvalidDataException($"meshopt_decode failed for mode {mode} (code {rc}).");

                if (mode == "ATTRIBUTES" && filter is not ("NONE" or ""))
                    ApplyFilter((IntPtr)dst, (nuint)count, (nuint)stride, filter);
            }
        }
    }

    private static void ApplyFilter(IntPtr buffer, nuint count, nuint stride, string filter)
    {
        switch (filter)
        {
            case "OCTAHEDRAL":
                MeshoptNative.meshopt_decodeFilterOct(buffer, count, stride);
                break;
            case "QUATERNION":
                MeshoptNative.meshopt_decodeFilterQuat(buffer, count, stride);
                break;
            case "EXPONENTIAL":
                MeshoptNative.meshopt_decodeFilterExp(buffer, count, stride);
                break;
            default:
                throw new NotSupportedException($"Unsupported meshopt filter: {filter}");
        }
    }

    private static void StripMeshoptExtensions(JsonNode json)
    {
        RemoveExtensionName(json, "extensionsUsed");
        RemoveExtensionName(json, "extensionsRequired");

        if (json["buffers"] is JsonArray buffers)
        {
            while (buffers.Count > 1)
                buffers.RemoveAt(buffers.Count - 1);

            if (buffers[0] is JsonObject buf)
                buf["extensions"]?.AsObject()?.Remove(Extension);
        }

        if (json["bufferViews"] is JsonArray views)
        {
            foreach (var v in views)
            {
                if (v is not JsonObject view) continue;
                view["buffer"] = 0;
                view["extensions"]?.AsObject()?.Remove(Extension);
                if (view["extensions"] is JsonObject exts && exts.Count == 0)
                    view.Remove("extensions");
            }
        }
    }

    private static void RemoveExtensionName(JsonNode json, string arrayName)
    {
        if (json[arrayName] is not JsonArray arr) return;
        for (int i = arr.Count - 1; i >= 0; i--)
        {
            if (arr[i]?.GetValue<string>() == Extension)
                arr.RemoveAt(i);
        }
        if (arr.Count == 0)
            json.AsObject().Remove(arrayName);
    }

    private static void WriteGlb(string path, JsonNode json, byte[] bin)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var jsonPad   = (4 - jsonBytes.Length % 4) % 4;
        var jsonChunk = jsonBytes.Length + jsonPad;
        var binPad    = (4 - bin.Length % 4) % 4;
        var total     = 12 + 8 + jsonChunk + 8 + bin.Length + binPad;

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

        File.WriteAllBytes(path, outBytes);
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
}