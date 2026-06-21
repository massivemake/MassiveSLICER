using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public sealed class MillingBridgeSnapshotTest
{
    const string SampleJson = """
        {
          "ok": true,
          "io": {
            "DI_04_gateOpenStop": false,
            "DI_05_SS1standstill": true,
            "DO_01_redLamp": false,
            "DO_02_yellowLamp": true,
            "DO_03_greenLamp": false
          }
        }
        """;

    [Fact]
    public void ParseReadResponse_reads_milling_io_names()
    {
        var snap = MillingModbusClient.ParseReadResponse(SampleJson);

        Assert.True(snap.Ok);
        Assert.False((bool)snap.Io["DI_04_gateOpenStop"]!);
        Assert.True((bool)snap.Io["DI_05_SS1standstill"]!);
        Assert.True((bool)snap.Io["DO_02_yellowLamp"]!);
    }

    [Fact]
    public void MillingPollKeys_match_catalog()
    {
        var catalogKeys = Lfam3LiveIoCatalog.Default.Sections
            .First(s => s.Title == "Milling Spindle")
            .Signals.Select(s => s.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in Lfam3LiveIoCatalog.MillingPollKeys)
            Assert.Contains(key, catalogKeys);
    }
}