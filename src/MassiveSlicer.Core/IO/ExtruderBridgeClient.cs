using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// TCP/JSON client for lfam-monitor.service on the extruder RevPi (port 8765).
/// Protocol mirrors MassiveCONNECT <c>monitor.py</c> <c>_bridge_read</c> / write helpers.
/// </summary>
public sealed class ExtruderBridgeClient
{
    public const int DefaultPort = 8765;
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    public async Task<ExtruderBridgeSnapshot> ReadAsync(
        string host,
        int port = DefaultPort,
        CancellationToken ct = default)
    {
        var json = await ExchangeAsync(host, port, """{"cmd":"read"}""", maxBytes: 131_072, ct);
        return ParseSnapshot(json);
    }

    public async Task<bool> TryWriteDigitalAsync(
        string host,
        string name,
        bool value,
        int port = DefaultPort,
        CancellationToken ct = default)
    {
        var req = JsonSerializer.Serialize(new { cmd = "write", name, value });
        var json = await ExchangeAsync(host, port, req, maxBytes: 512, ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    static async Task<string> ExchangeAsync(
        string host,
        int port,
        string requestJson,
        int maxBytes,
        CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DefaultTimeout);
        await client.ConnectAsync(host, port, timeoutCts.Token);

        var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes(requestJson + "\n");
        await stream.WriteAsync(payload, timeoutCts.Token);

        var buf = new byte[4096];
        var sb = new StringBuilder();
        while (sb.Length < maxBytes)
        {
            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), timeoutCts.Token);
            if (n == 0) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            if (sb.ToString().Contains('\n')) break;
        }

        return sb.ToString().Trim();
    }

    /// <summary>Parses a <c>{"cmd":"read"}</c> JSON line (for tests and diagnostics).</summary>
    public static ExtruderBridgeSnapshot ParseReadResponse(string json) => ParseSnapshot(json);

    static ExtruderBridgeSnapshot ParseSnapshot(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            string? error = root.TryGetProperty("error", out var errEl)
                ? errEl.GetString()
                : null;

            var io = ParseObjectDict(root, "io");
            var modbus = ParseObjectDict(root, "modbus");

            bool modbusConnected = modbus.TryGetValue("modbus_connected", out var mc) && mc is true;
            string? modbusError = modbus.TryGetValue("modbus_error", out var me) ? me?.ToString() : null;
            modbus.Remove("modbus_connected");
            modbus.Remove("modbus_error");

            return new ExtruderBridgeSnapshot(ok, error, io, modbus, modbusConnected, modbusError);
        }
        catch (Exception ex)
        {
            return new ExtruderBridgeSnapshot(
                false, ex.Message,
                new Dictionary<string, object?>(),
                new Dictionary<string, object?>(),
                false, null);
        }
    }

    static Dictionary<string, object?> ParseObjectDict(JsonElement root, string name)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
                dict[prop.Name] = JsonToObject(prop.Value);
        }
        return dict;
    }

    static object? JsonToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True  => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when el.TryGetInt64(out var i) => i,
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        _ => el.GetRawText(),
    };
}

/// <summary>One <c>{"cmd":"read"}</c> response from the extruder bridge.</summary>
public sealed record ExtruderBridgeSnapshot(
    bool Ok,
    string? Error,
    IReadOnlyDictionary<string, object?> Io,
    IReadOnlyDictionary<string, object?> Modbus,
    bool ModbusConnected,
    string? ModbusError);