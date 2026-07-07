using System.Text;

namespace MassiveSlicer.App.Plasticity;

/// <summary>
/// Shared binary codec for the Plasticity live-bridge wire format (little-endian,
/// 4-byte message types, length-prefixed strings padded to 4 bytes). Used by both
/// the Plasticity client (we connect to Plasticity's server) and the MassiveBRAIN
/// server (Blender/Rhino addons connect to us and push the same transaction frames).
/// </summary>
internal static class PlasticityWire
{
    /// <summary>
    /// Decodes a transaction body (filename + version + item list) starting at
    /// <paramref name="off"/>, appending Add/Update objects and Delete ids.
    /// </summary>
    public static void DecodeTransaction(
        byte[] d, ref int off, int end,
        List<PlasticityObject> added, List<int> deleted)
    {
        int fnLen = (int)ReadU32(d, ref off, end);
        off += fnLen + Pad4(fnLen);                    // skip filename + padding
        _ = ReadU32(d, ref off, end);                  // version
        int numMessages = (int)ReadU32(d, ref off, end);

        for (int i = 0; i < numMessages; i++)
        {
            if (off + 4 > end) break;
            int itemLen = (int)ReadU32(d, ref off, end);
            int itemEnd = Math.Min(off + itemLen, end);
            DecodeItem(d, off, itemEnd, added, deleted);
            off = itemEnd;
        }
    }

    private static void DecodeItem(byte[] d, int start, int end, List<PlasticityObject> added, List<int> deleted)
    {
        int off = start;
        var sub = (PlasticityMessageType)ReadU32(d, ref off, end);

        switch (sub)
        {
            case PlasticityMessageType.Add:
            case PlasticityMessageType.Update:
            {
                int n = (int)ReadU32(d, ref off, end);
                for (int i = 0; i < n && off < end; i++)
                {
                    var obj = DecodeObject(d, ref off, end);
                    if (obj is not null) added.Add(obj);
                }
                break;
            }
            case PlasticityMessageType.Delete:
            {
                int n = (int)ReadU32(d, ref off, end);
                for (int i = 0; i < n && off + 4 <= end; i++)
                    deleted.Add((int)ReadU32(d, ref off, end));
                break;
            }
        }
    }

    private static PlasticityObject? DecodeObject(byte[] d, ref int off, int end)
    {
        var objType   = (PlasticityObjectType)ReadU32(d, ref off, end);
        int id        = (int)ReadU32(d, ref off, end);
        int version   = (int)ReadU32(d, ref off, end);
        _             = ReadU32(d, ref off, end);      // parent id (signed)
        _             = ReadU32(d, ref off, end);      // material id (signed)
        _             = ReadU32(d, ref off, end);      // flags
        int nameLen   = (int)ReadU32(d, ref off, end);
        string name   = Encoding.UTF8.GetString(d, off, Math.Clamp(nameLen, 0, end - off));
        off += nameLen + Pad4(nameLen);

        // Only solids and sheets carry mesh; groups/wires/empties have no geometry in this stream.
        if (objType != PlasticityObjectType.Solid && objType != PlasticityObjectType.Sheet)
            return null;

        int nv        = (int)ReadU32(d, ref off, end);
        var positions = ReadFloats(d, ref off, nv * 3, end);
        int nf        = (int)ReadU32(d, ref off, end);
        var indices   = ReadInts(d, ref off, nf * 3, end);
        int nn        = (int)ReadU32(d, ref off, end);
        var normals   = ReadFloats(d, ref off, nn * 3, end);
        int ng        = (int)ReadU32(d, ref off, end);
        off += ng * 4;                                 // skip groups
        int nfi       = (int)ReadU32(d, ref off, end);
        off += nfi * 4;                                // skip face ids

        return new PlasticityObject
        {
            Id        = id,
            Version   = version,
            Name      = string.IsNullOrWhiteSpace(name) ? $"Object {id}" : name,
            Type      = objType,
            Positions = positions,
            Indices   = indices,
            Normals   = normals,
        };
    }

    // -- Primitive readers (little-endian, bounds-checked) -----------------

    public static uint ReadU32(byte[] d, ref int off, int end)
    {
        if (off + 4 > end) throw new InvalidDataException("message truncated");
        uint v = BitConverter.ToUInt32(d, off);
        off += 4;
        return v;
    }

    public static float[] ReadFloats(byte[] d, ref int off, int count, int end)
    {
        if (count <= 0) return [];
        if (off + count * 4 > end) throw new InvalidDataException("float array truncated");
        var r = new float[count];
        Buffer.BlockCopy(d, off, r, 0, count * 4);     // little-endian host: raw copy is correct
        off += count * 4;
        return r;
    }

    public static int[] ReadInts(byte[] d, ref int off, int count, int end)
    {
        if (count <= 0) return [];
        if (off + count * 4 > end) throw new InvalidDataException("int array truncated");
        var r = new int[count];
        Buffer.BlockCopy(d, off, r, 0, count * 4);
        off += count * 4;
        return r;
    }

    /// <summary>Bytes of zero-padding that follow a length-prefixed string to reach a 4-byte boundary.</summary>
    public static int Pad4(int len) => (4 - (len % 4)) % 4;
}
