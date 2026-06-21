using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class ExtruderBridgeSnapshotTest
{
    const string SampleJson = """
        {
          "ok": true,
          "ts": 1710000000,
          "io": {
            "DI_02_safetyGate": true,
            "DO_05_motorEnable": false,
            "O_1": true,
            "RTDValue_1": 2250,
            "AI_09_MIO_extruderMotorVel": 4500
          },
          "modbus": {
            "modbus_connected": true,
            "hr_30201": 218,
            "hr_30101": 220
          }
        }
        """;

    [Fact]
    public void ParseReadResponse_splits_io_and_modbus()
    {
        var snap = ExtruderBridgeClient.ParseReadResponse(SampleJson);

        Assert.True(snap.Ok);
        Assert.True(snap.Io["DI_02_safetyGate"] is true);
        Assert.True(snap.Io["O_1"] is true);
        Assert.Equal(2250L, snap.Io["RTDValue_1"]);
        Assert.True(snap.ModbusConnected);
        Assert.Equal(218L, snap.Modbus["hr_30201"]);
        Assert.Equal(220L, snap.Modbus["hr_30101"]);
        Assert.False(snap.Modbus.ContainsKey("modbus_connected"));
    }

    [Fact]
    public void FormatDisplay_scales_bridge_analog_and_modbus_temps()
    {
        var rtd = new LiveIoSignalConfig(
            "Nozzle RTD 1", "RTDValue_1", LiveIoSignalKind.AnalogInput,
            LiveIoSource.ExtruderBridge, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC);
        var mio = new LiveIoSignalConfig(
            "Motor velocity", "AI_09_MIO_extruderMotorVel", LiveIoSignalKind.AnalogInput,
            LiveIoSource.ExtruderBridge, Unit: "V", ValueFormat: LiveIoValueFormat.Millivolt);
        var zone = new LiveIoSignalConfig(
            "Zone 1 actual", "hr_30201", LiveIoSignalKind.AnalogInput,
            LiveIoSource.ExtruderModbus, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC);

        Assert.Equal("225.0 °C", LiveIoValueFormatter.FormatDisplay(rtd, "2250"));
        Assert.Equal("4.500 V", LiveIoValueFormatter.FormatDisplay(mio, "4500"));
        Assert.Equal("218.0 °C", LiveIoValueFormatter.FormatDisplay(zone, "218"));
    }
}