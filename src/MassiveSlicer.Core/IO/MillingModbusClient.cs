namespace MassiveSlicer.Core.IO;

/// <summary>
/// LFAM 3 milling cabinet I/O via lfam-monitor JSON bridge on the milling RevPi (TCP:8765).
/// Signal keys match MassiveCONNECT <c>modbus_monitor.py</c> <c>MILLING_IO</c> RevPi DIO names
/// returned in the bridge <c>io</c> dict (same wire protocol as <see cref="ExtruderBridgeClient"/>).
/// </summary>
public sealed class MillingModbusClient
{
    public const int DefaultPort = 8765;
    public const int DefaultPollIntervalMs = 3000;

    readonly ExtruderBridgeClient _bridge = new();

    public async Task<MillingBridgeSnapshot> ReadAsync(
        string host,
        int port = DefaultPort,
        CancellationToken ct = default)
    {
        var snap = await _bridge.ReadAsync(host, port, ct);
        return MillingBridgeSnapshot.FromBridge(snap);
    }

    /// <summary>Parses a bridge <c>{"cmd":"read"}</c> JSON line (for tests).</summary>
    public static MillingBridgeSnapshot ParseReadResponse(string json)
        => MillingBridgeSnapshot.FromBridge(ExtruderBridgeClient.ParseReadResponse(json));
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