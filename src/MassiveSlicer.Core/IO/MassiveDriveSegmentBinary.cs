using System.Buffers.Binary;
using System.Text;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Fixed-stride little-endian <c>segments.bin</c> for massivedrive.job/v2.
/// See <c>docs/massivedrive-job-v2.md</c>. Do not change stride or field offsets
/// without bumping the file version and coordinating MassiveDRIVE.
/// </summary>
public static class MassiveDriveSegmentBinary
{
    public const int Version = 2;
    public const int HeaderSize = 32;
    public const int RecordStride = 80;

    public const byte KindPrint = 1;
    public const byte KindTravel = 2;
    public const byte KindMill = 3;

    public const byte FlagReverse = 1 << 0;
    public const byte FlagLayerChange = 1 << 1;
    public const byte FlagWipe = 1 << 2;
    public const byte FlagResumeRamp = 1 << 3;
    public const byte FlagPreTravelStart = 1 << 4;
    public const byte FlagPostTravelEnd = 1 << 5;

    static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("MDSEG2\0\0");

    public static ReadOnlySpan<byte> Magic => MagicBytes;

    public static byte KindCode(string kind) => kind switch
    {
        "travel" => KindTravel,
        "mill" => KindMill,
        _ => KindPrint,
    };

    public static string KindName(byte code) => code switch
    {
        KindTravel => "travel",
        KindMill => "mill",
        _ => "print",
    };

    public static byte PackFlags(MassiveDriveSegment seg)
    {
        byte f = 0;
        if (seg.Reverse) f |= FlagReverse;
        if (seg.LayerChange) f |= FlagLayerChange;
        if (seg.Wipe) f |= FlagWipe;
        if (seg.ResumeRamp) f |= FlagResumeRamp;
        if (seg.PreTravelStart) f |= FlagPreTravelStart;
        if (seg.PostTravelEnd) f |= FlagPostTravelEnd;
        return f;
    }

    public static void WriteHeader(Span<byte> dest, int recordCount)
    {
        if (dest.Length < HeaderSize)
            throw new ArgumentException("header buffer too small", nameof(dest));
        dest[..HeaderSize].Clear();
        Magic.CopyTo(dest);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[12..], RecordStride);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[16..], (uint)recordCount);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[20..], 1); // flags: records include rpm_pct
    }

    public static void WriteRecord(Span<byte> dest, MassiveDriveSegment seg)
    {
        if (dest.Length < RecordStride)
            throw new ArgumentException("record buffer too small", nameof(dest));
        dest[..RecordStride].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(dest[0..], seg.Index);
        dest[4] = KindCode(seg.Kind);
        dest[5] = PackFlags(seg);
        BinaryPrimitives.WriteInt16LittleEndian(dest[6..], (short)Math.Clamp(seg.Layer, short.MinValue, short.MaxValue));
        WritePose(dest[8..], seg.From);
        WritePose(dest[32..], seg.To);
        BinaryPrimitives.WriteSingleLittleEndian(dest[56..], (float)seg.SpeedMmS);
        BinaryPrimitives.WriteSingleLittleEndian(dest[60..], (float)seg.FlowScale);
        int rpm = Math.Clamp(seg.RpmPct, 0, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[64..], (ushort)rpm);
    }

    public static MassiveDriveSegment ReadRecord(ReadOnlySpan<byte> src)
    {
        if (src.Length < RecordStride)
            throw new ArgumentException("record buffer too small", nameof(src));
        var flags = src[5];
        var seg = new MassiveDriveSegment
        {
            Index = BinaryPrimitives.ReadInt32LittleEndian(src),
            Kind = KindName(src[4]),
            Layer = BinaryPrimitives.ReadInt16LittleEndian(src[6..]),
            From = ReadPose(src[8..]),
            To = ReadPose(src[32..]),
            SpeedMmS = BinaryPrimitives.ReadSingleLittleEndian(src[56..]),
            FlowScale = BinaryPrimitives.ReadSingleLittleEndian(src[60..]),
            RpmPct = BinaryPrimitives.ReadUInt16LittleEndian(src[64..]),
            Reverse = (flags & FlagReverse) != 0,
            LayerChange = (flags & FlagLayerChange) != 0,
            Wipe = (flags & FlagWipe) != 0,
            ResumeRamp = (flags & FlagResumeRamp) != 0,
            PreTravelStart = (flags & FlagPreTravelStart) != 0,
            PostTravelEnd = (flags & FlagPostTravelEnd) != 0,
        };
        return seg;
    }

    public static (int version, int stride, int count) ReadHeader(ReadOnlySpan<byte> src)
    {
        if (src.Length < HeaderSize)
            throw new ArgumentException("header buffer too small", nameof(src));
        if (!src[..8].SequenceEqual(Magic))
            throw new InvalidDataException("segments.bin magic is not MDSEG2");
        int version = (int)BinaryPrimitives.ReadUInt32LittleEndian(src[8..]);
        int stride = (int)BinaryPrimitives.ReadUInt32LittleEndian(src[12..]);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(src[16..]);
        if (version != Version)
            throw new InvalidDataException($"segments.bin version {version} (expected {Version})");
        if (stride != RecordStride)
            throw new InvalidDataException($"segments.bin stride {stride} (expected {RecordStride})");
        return (version, stride, count);
    }

    static void WritePose(Span<byte> dest, MassiveDrivePose p)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dest[0..], (float)p.X);
        BinaryPrimitives.WriteSingleLittleEndian(dest[4..], (float)p.Y);
        BinaryPrimitives.WriteSingleLittleEndian(dest[8..], (float)p.Z);
        BinaryPrimitives.WriteSingleLittleEndian(dest[12..], (float)p.A);
        BinaryPrimitives.WriteSingleLittleEndian(dest[16..], (float)p.B);
        BinaryPrimitives.WriteSingleLittleEndian(dest[20..], (float)p.C);
    }

    static MassiveDrivePose ReadPose(ReadOnlySpan<byte> src) => new(
        BinaryPrimitives.ReadSingleLittleEndian(src[0..]),
        BinaryPrimitives.ReadSingleLittleEndian(src[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(src[8..]),
        BinaryPrimitives.ReadSingleLittleEndian(src[12..]),
        BinaryPrimitives.ReadSingleLittleEndian(src[16..]),
        BinaryPrimitives.ReadSingleLittleEndian(src[20..]));
}
