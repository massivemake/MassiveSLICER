using MassiveSlicer.Core.IO;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

public class RccDebugTest(ITestOutputHelper output)
{
    [Theory]
    [InlineData("reslib_kuka_01.rcc")]
    [InlineData("reslib_kuka_02.rcc")]
    [InlineData("reslib_kuka_03.rcc")]
    public void DumpHeader(string fileName)
    {
        string? path = FindAsset(fileName);
        if (path is null) { output.WriteLine($"SKIP: {fileName} not found."); return; }

        byte[] raw = File.ReadAllBytes(path);
        output.WriteLine($"\n{fileName}  ({raw.Length:N0} bytes)");
        output.WriteLine($"  magic:    0x{RB32(raw,0):X8}  ({System.Text.Encoding.ASCII.GetString(raw, 0, 4)})");
        output.WriteLine($"  version:  {RB32(raw,4)}");
        output.WriteLine($"  treeOff:  {RB32(raw,8)}");
        output.WriteLine($"  dataOff:  {RB32(raw,12)}");
        output.WriteLine($"  namesOff: {RB32(raw,16)}");

        // Print root node (tree[0]) raw bytes
        int treeOff = (int)RB32(raw, 8);
        output.WriteLine($"  root node bytes: {BitConverter.ToString(raw, treeOff, Math.Min(22, raw.Length - treeOff))}");

        uint flags = RB16(raw, treeOff + 4);
        output.WriteLine($"  root flags: 0x{flags:X4}  isDir={( flags & 2) != 0}  isComp={(flags & 1) != 0}");
        if ((flags & 2) != 0)
        {
            uint childCount = RB32(raw, treeOff + 6);
            uint firstChild = RB32(raw, treeOff + 10);
            output.WriteLine($"  root childCount={childCount}  firstChild={firstChild}");
        }
    }

    [Theory]
    [InlineData("reslib_kuka_01.rcc")]
    [InlineData("reslib_kuka_02.rcc")]
    [InlineData("reslib_kuka_03.rcc")]
    public void PeekAllEntries(string fileName)
    {
        string? path = FindAsset(fileName);
        if (path is null) { output.WriteLine($"SKIP: {fileName}"); return; }

        var files = RccExtractor.Extract(File.ReadAllBytes(path));
        output.WriteLine($"\n{fileName} -- {files.Count} entries:");
        foreach (var (vpath, data) in files.OrderBy(kv => kv.Key))
        {
            string sig   = BitConverter.ToString(data, 0, Math.Min(16, data.Length));
            string ascii = System.Text.Encoding.ASCII.GetString(
                data.Take(16).Select(b => b is >= 32 and < 127 ? b : (byte)'?').ToArray());
            output.WriteLine($"  {vpath}  ({data.Length:N0} B)  [{sig}]  '{ascii}'");
        }
    }

    private static string? FindAsset(string fileName) =>
        new[]
        {
            Path.Combine(AppContext.BaseDirectory, "../../../../..", "assets", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", fileName),
        }.FirstOrDefault(File.Exists);

    private static uint   RB32(byte[] b, int i) =>
        ((uint)b[i] << 24) | ((uint)b[i+1] << 16) | ((uint)b[i+2] << 8) | b[i+3];
    private static ushort RB16(byte[] b, int i) =>
        (ushort)(((uint)b[i] << 8) | b[i+1]);
}
