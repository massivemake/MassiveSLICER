using System.Net.WebSockets;
using System.Text;

namespace MassiveSlicer.App.Plasticity;

/// <summary>
/// WebSocket client for the Plasticity server, reimplementing the wire protocol used by the
/// official Plasticity Blender bridge. On connect it handshakes, requests the current object
/// list, and (optionally) subscribes so the server pushes a transaction on every edit in
/// Plasticity. Solid/sheet geometry arrives inline in those transactions — no refacet round-trip
/// is required for a live update. All events are raised on a background receive thread; callers
/// must marshal to the UI thread themselves.
/// </summary>
internal sealed class PlasticityClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _messageId;

    /// <summary>True while a socket is open.</summary>
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>Raised once the socket opens (before the initial list/subscribe are sent).</summary>
    public event Action? Connected;

    /// <summary>Raised when the socket closes or errors; argument is a short reason.</summary>
    public event Action<string>? Disconnected;

    /// <summary>A batch of added/updated solid or sheet objects (with geometry).</summary>
    public event Action<IReadOnlyList<PlasticityObject>>? ObjectsReceived;

    /// <summary>Plasticity object ids that were deleted in the source document.</summary>
    public event Action<IReadOnlyList<int>>? ObjectsDeleted;

    /// <summary>Diagnostic messages for the app console.</summary>
    public event Action<string>? Log;

    /// <summary>Opens the socket to <paramref name="address"/> (host:port), handshakes, lists the
    /// current objects, and subscribes to live updates when <paramref name="subscribeLive"/> is set.</summary>
    public async Task ConnectAsync(string address, bool subscribeLive, CancellationToken ct)
    {
        await DisconnectAsync();

        var uri = BuildUri(address);
        var ws = new ClientWebSocket();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await ws.ConnectAsync(uri, _cts.Token);
        }
        catch (Exception ex)
        {
            ws.Dispose();
            Disconnected?.Invoke($"connect failed: {ex.Message}");
            throw;
        }

        _ws = ws;
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        Connected?.Invoke();

        await SendSimpleAsync(PlasticityMessageType.Handshake);
        await SendSimpleAsync(PlasticityMessageType.ListVisible);
        if (subscribeLive)
            await SendSimpleAsync(PlasticityMessageType.SubscribeAll);
    }

    /// <summary>Re-requests the full visible object list (re-pulls current geometry).</summary>
    public Task RefreshAsync()      => SendSimpleAsync(PlasticityMessageType.ListVisible);

    /// <summary>Starts receiving live transactions on every Plasticity edit.</summary>
    public Task SubscribeAsync()    => SendSimpleAsync(PlasticityMessageType.SubscribeAll);

    /// <summary>Stops receiving live transactions.</summary>
    public Task UnsubscribeAsync()  => SendSimpleAsync(PlasticityMessageType.UnsubscribeAll);

    public async Task DisconnectAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }

        var ws = _ws;
        _ws = null;
        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
            }
            catch { /* ignore */ }
            ws.Dispose();
        }

        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    public void Dispose() => _ = DisconnectAsync();

    // -- Outbound ----------------------------------------------------------

    private async Task SendSimpleAsync(PlasticityMessageType type)
    {
        var ws = _ws;
        var cts = _cts;
        if (ws is null || cts is null || ws.State != WebSocketState.Open) return;

        int id = Interlocked.Increment(ref _messageId);
        var buf = new byte[8];
        BitConverter.TryWriteBytes(buf.AsSpan(0), (uint)type);
        BitConverter.TryWriteBytes(buf.AsSpan(4), (uint)id);
        try
        {
            await ws.SendAsync(buf, WebSocketMessageType.Binary, endOfMessage: true, cts.Token);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"send {type} failed: {ex.Message}");
        }
    }

    // -- Inbound -----------------------------------------------------------

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null) return;

        var frame = new byte[64 * 1024];
        using var ms = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(frame, ct);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        Disconnected?.Invoke("server closed connection");
                        return;
                    }
                    ms.Write(frame, 0, res.Count);
                }
                while (!res.EndOfMessage);

                if (ms.Length < 4) continue;
                try
                {
                    Decode(ms.GetBuffer(), (int)ms.Length);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"decode error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { /* normal on disconnect */ }
        catch (Exception ex)
        {
            Disconnected?.Invoke(ex.Message);
        }
    }

    private void Decode(byte[] d, int len)
    {
        int off = 0;
        var type = (PlasticityMessageType)ReadU32(d, ref off, len);

        switch (type)
        {
            case PlasticityMessageType.Transaction:
                DecodeTransaction(d, ref off, len);
                break;

            case PlasticityMessageType.ListAll:
            case PlasticityMessageType.ListSome:
            case PlasticityMessageType.ListVisible:
                _ = ReadU32(d, ref off, len);          // message id
                int code = (int)ReadU32(d, ref off, len);
                if (code != 200) { Log?.Invoke($"list failed (code {code})"); return; }
                DecodeTransaction(d, ref off, len);
                break;

            // Version/file notifications and the handshake reply carry no geometry — the server
            // pushes a transaction separately when subscribed, so nothing to do here.
            case PlasticityMessageType.NewVersion:
            case PlasticityMessageType.NewFile:
            case PlasticityMessageType.Handshake:
            default:
                break;
        }
    }

    private void DecodeTransaction(byte[] d, ref int off, int end)
    {
        var added   = new List<PlasticityObject>();
        var deleted = new List<int>();
        PlasticityWire.DecodeTransaction(d, ref off, end, added, deleted);
        if (added.Count   > 0) ObjectsReceived?.Invoke(added);
        if (deleted.Count > 0) ObjectsDeleted?.Invoke(deleted);
    }

    private static uint ReadU32(byte[] d, ref int off, int end) => PlasticityWire.ReadU32(d, ref off, end);

    private static Uri BuildUri(string address)
    {
        address = address.Trim();
        if (!address.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            address = "ws://" + address;
        return new Uri(address);
    }
}
