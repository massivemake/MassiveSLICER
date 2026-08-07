using System.Text.Json;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// LFAM 3 milling cabinet I/O via lfam-monitor JSON bridge on the milling RevPi (TCP:8765).
/// Signal keys match MassiveCONNECT <c>modbus_monitor.py</c> <c>MILLING_IO</c> RevPi DIO names
/// returned in the bridge <c>io</c> dict (same wire protocol as <see cref="ExtruderBridgeClient"/>).
/// Also exposes ATV340 spindle status / RPM setpoint (<c>spindle</c> / <c>set_rpm</c> cmds).
/// </summary>
public sealed class MillingModbusClient
{
    public const int DefaultPort = 8765;
    public const int DefaultPollIntervalMs = 3000;
    public const int SpindleRpmHardMax = 24000;

    readonly ExtruderBridgeClient _bridge = new();

    public async Task<MillingBridgeSnapshot> ReadAsync(
        string host,
        int port = DefaultPort,
        CancellationToken ct = default)
    {
        var snap = await _bridge.ReadAsync(host, port, ct);
        return MillingBridgeSnapshot.FromBridge(snap);
    }

    /// <summary>Poll ATV340 status from the milling bridge (<c>{"cmd":"spindle"}</c>).</summary>
    public async Task<SpindleBridgeStatus> ReadSpindleAsync(
        string host,
        int port = DefaultPort,
        CancellationToken ct = default)
    {
        var json = await _bridge.SendCommandAsync(host, """{"cmd":"spindle"}""", port, 8192, ct);
        return SpindleBridgeStatus.Parse(json);
    }

    /// <summary>Command ATV speed reference 0..24000 rpm via bridge <c>set_rpm</c>.</summary>
    public async Task<SpindleBridgeStatus> SetSpindleRpmAsync(
        string host,
        int rpm,
        int port = DefaultPort,
        CancellationToken ct = default)
    {
        if (rpm < 0 || rpm > SpindleRpmHardMax)
            throw new ArgumentOutOfRangeException(nameof(rpm), rpm, $"rpm must be 0..{SpindleRpmHardMax}");
        var req = JsonSerializer.Serialize(new { cmd = "set_rpm", rpm });
        var json = await _bridge.SendCommandAsync(host, req, port, 16384, ct);
        return SpindleBridgeStatus.Parse(json);
    }

    /// <summary>Parses a bridge <c>{"cmd":"read"}</c> JSON line (for tests).</summary>
    public static MillingBridgeSnapshot ParseReadResponse(string json)
        => MillingBridgeSnapshot.FromBridge(ExtruderBridgeClient.ParseReadResponse(json));
}

/// <summary>ATV340 status / setpoint from milling bridge spindle commands.</summary>
public sealed record SpindleBridgeStatus(
    bool Ok,
    string? Error,
    double SpeedRpm,
    double SetpointRpm,
    string State,
    bool Fault)
{
    public static SpindleBridgeStatus Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            string? error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;

            // set_rpm returns fields at top level; spindle cmd nests under "spindle"
            JsonElement sp = root;
            if (root.TryGetProperty("spindle", out var nested) && nested.ValueKind == JsonValueKind.Object)
                sp = nested;

            double speed = ReadDouble(sp, "speed_rpm");
            double setp = ReadDouble(sp, "setpoint_rpm");
            if (setp == 0 && sp.TryGetProperty("lfrd_rpm", out _))
                setp = ReadDouble(sp, "lfrd_rpm");
            if (setp == 0 && sp.TryGetProperty("requested_rpm", out _))
                setp = ReadDouble(sp, "requested_rpm");
            if (setp == 0 && root.TryGetProperty("requested_rpm", out _))
                setp = ReadDouble(root, "requested_rpm");
            // After set_rpm, result embeds under "result"
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (setp == 0) setp = ReadDouble(result, "requested_rpm");
                if (setp == 0) setp = ReadDouble(result, "modbus_lfrd");
                if (speed == 0) speed = ReadDouble(result, "rfrd_rpm");
            }

            string state = sp.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
            bool fault = sp.TryGetProperty("fault", out var f) && f.ValueKind == JsonValueKind.True;
            return new SpindleBridgeStatus(ok, error, speed, setp, state, fault);
        }
        catch (Exception ex)
        {
            return new SpindleBridgeStatus(false, ex.Message, 0, 0, "", false);
        }
    }

    static double ReadDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return 0;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDouble(),
            JsonValueKind.String when double.TryParse(p.GetString(), out var d) => d,
            _ => 0,
        };
    }
}

/// <summary>Milling cabinet bridge response — only the flat <c>io</c> bucket is used.</summary>
public sealed record MillingBridgeSnapshot(
    bool Ok,
    string? Error,
    IReadOnlyDictionary<string, object?> Io)
{
    internal static MillingBridgeSnapshot FromBridge(ExtruderBridgeSnapshot snap)
        => new(snap.Ok, snap.Error, snap.Io);
}