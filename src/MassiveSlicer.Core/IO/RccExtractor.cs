using System.IO.Compression;
using System.Text;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Extracts embedded files from a Qt binary resource file (.rcc).
/// Supports format versions 1 and 2 (Qt 4.x through 6.x).
/// Zlib-compressed entries are decompressed automatically.
/// </summary>
public static class RccExtractor
{
    private const uint Magic = 0x71726573u; // "qres"

    // Flags stored in each tree node.
    private const ushort FlagCompressedZlib = 0x01;
    private const ushort FlagDirectory      = 0x02;
    private const ushort FlagCompressedZstd = 0x04;

    /// <summary>
    /// Extracts all files from <paramref name="rccPath"/> into <paramref name="outputDir"/>.
    /// Returns the list of files written.
    /// </summary>
    public static IReadOnlyList<string> ExtractToDirectory(string rccPath, string outputDir)
    {
        var files = Extract(File.ReadAllBytes(rccPath));
        Directory.CreateDirectory(outputDir);
        var written = new List<string>();

        foreach (var (virtualPath, data) in files)
        {
            // Strip leading slash from Qt virtual paths (e.g. "/models/robot.glb")
            string relative = virtualPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string dest     = Path.Combine(outputDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, data);
            written.Add(dest);
        }
        return written;
    }

    /// <summary>
    /// Parses a .rcc file and returns all embedded files as a dictionary
    /// keyed by their virtual path (e.g. <c>/models/robot.glb</c>).
    /// </summary>
    public static Dictionary<string, byte[]> Extract(byte[] raw)
    {
        if (raw.Length < 20)
            throw new InvalidDataException("File too small to be a Qt RCC file.");

        uint magic = RB32(raw, 0);
        if (magic != Magic)
            throw new InvalidDataException($"Not a Qt RCC file (magic 0x{magic:X8}).");

        uint version  = RB32(raw, 4);
        uint treeOff  = RB32(raw, 8);
        uint dataOff  = RB32(raw, 12);
        uint namesOff = RB32(raw, 16);

        // Build a node-index -> byte-offset table so we can resolve firstChild indices.
        // v1: all nodes are 14 bytes.
        // v2+: directory nodes stay 14 bytes; file nodes grow to 22 (extra 8 = lastModified).
        int[] nodeOffsets = BuildNodeOffsets(raw, treeOff, dataOff, namesOff, version);

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        WalkDirectory(raw, nodeOffsets, 0, "", dataOff, namesOff, result);
        return result;
    }

    // -- Tree walking ----------------------------------------------------------

    // Walk the root node (node 0) without adding its name to the path.
    private static void WalkDirectory(
        byte[] raw, int[] nodeOffsets, int idx, string path,
        uint dataOff, uint namesOff, Dictionary<string, byte[]> result)
    {
        if ((uint)idx >= (uint)nodeOffsets.Length) return;
        int pos = nodeOffsets[idx];

        uint   nameOff = RB32(raw, pos);
        ushort flags   = RB16(raw, pos + 4);
        bool   isDir   = (flags & FlagDirectory) != 0;

        // Append this node's name to the path, except for the root (idx == 0 with empty path).
        string name     = ReadName(raw, namesOff, nameOff);
        string fullPath = path.Length == 0 && idx == 0 ? "" : (path.Length == 0 ? name : $"{path}/{name}");

        if (isDir)
        {
            uint childCount = RB32(raw, pos + 6);
            uint firstChild = RB32(raw, pos + 10);
            for (uint i = 0; i < childCount; i++)
                WalkDirectory(raw, nodeOffsets, (int)(firstChild + i), fullPath, dataOff, namesOff, result);
        }
        else
        {
            // File: country(2) + language(2) + dataOffset(4) starting at pos+6
            uint fileDataOff = RB32(raw, pos + 10);
            int  dPos        = (int)dataOff + (int)fileDataOff;
            uint size        = RB32(raw, dPos);

            if ((long)dPos + 4 + size > raw.Length)
                return; // truncated or bad offset -- skip

            byte[] data = new byte[size];
            Buffer.BlockCopy(raw, dPos + 4, data, 0, (int)size);

            if ((flags & FlagCompressedZlib) != 0)
                data = ZlibDecompress(data);
            else if ((flags & FlagCompressedZstd) != 0)
                throw new NotSupportedException("Zstd-compressed RCC entries are not supported.");

            result[fullPath] = data;
        }
    }

    // -- Node offset table -----------------------------------------------------

    private static int[] BuildNodeOffsets(byte[] raw, uint treeOff, uint dataOff, uint namesOff, uint version)
    {
        // Determine where the tree section ends by finding the nearest section boundary above it.
        var others  = new[] { dataOff, namesOff }.Where(o => o > treeOff).ToArray();
        uint treeEnd = others.Length > 0 ? others.Min() : (uint)raw.Length;
        int  treeLen = (int)(treeEnd - treeOff);

        if (version == 1)
        {
            // v1: all nodes are 14 bytes.
            int count   = treeLen / 14;
            var offsets = new int[count];
            for (int i = 0; i < count; i++)
                offsets[i] = (int)treeOff + i * 14;
            return offsets;
        }
        else if (version >= 3)
        {
            // v3+: all nodes (dirs and files) are 22 bytes.
            int count   = treeLen / 22;
            var offsets = new int[count];
            for (int i = 0; i < count; i++)
                offsets[i] = (int)treeOff + i * 22;
            return offsets;
        }
        else
        {
            // v2: directory nodes = 14 bytes, file nodes = 22 bytes -- scan sequentially.
            var offsets = new List<int>();
            int pos = (int)treeOff;
            int end = (int)treeOff + treeLen;
            while (pos + 6 <= end)
            {
                offsets.Add(pos);
                ushort flags = RB16(raw, pos + 4);
                pos += (flags & FlagDirectory) != 0 ? 14 : 22;
            }
            return offsets.ToArray();
        }
    }

    // -- Names section ---------------------------------------------------------

    private static string ReadName(byte[] raw, uint namesOff, uint nameOff)
    {
        int    pos = (int)namesOff + (int)nameOff;
        ushort len = RB16(raw, pos);
        pos += 2 + 4; // skip length (2) + hash (4)

        // Qt stores names as UTF-16 BE; .NET strings are UTF-16 LE -- swap each pair.
        var chars = new byte[len * 2];
        for (int i = 0; i < len; i++)
        {
            chars[i * 2]     = raw[pos + i * 2 + 1];
            chars[i * 2 + 1] = raw[pos + i * 2];
        }
        return Encoding.Unicode.GetString(chars);
    }

    // -- Decompression ---------------------------------------------------------

    private static byte[] ZlibDecompress(byte[] data)
    {
        // Qt qCompress format: 4-byte BE uncompressed-size prefix, then a standard zlib stream.
        using var input  = new MemoryStream(data, 4, data.Length - 4);
        using var output = new MemoryStream();
        using var zs     = new ZLibStream(input, CompressionMode.Decompress);
        zs.CopyTo(output);
        return output.ToArray();
    }

    // -- Big-endian readers ----------------------------------------------------

    private static uint   RB32(byte[] b, int i) =>
        ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

    private static ushort RB16(byte[] b, int i) =>
        (ushort)(((uint)b[i] << 8) | b[i + 1]);
}
