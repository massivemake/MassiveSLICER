using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using MassiveSlicer.App.Plasticity;

namespace MassiveSlicer.App.MassiveBrain;

/// <summary>
/// MassiveBRAIN: a WebSocket server hosted by MassiveSLICER that DCC addons
/// (Blender, Rhino) connect to and push geometry, using the same binary wire
/// format as the Plasticity live bridge (Handshake=100, Transaction=0 with
/// Add/Update/Delete items carrying full meshes inline, metres, Z-up).
///
/// Data direction is the inverse of the Plasticity bridge: here the DCC owns
/// the geometry and pushes transactions on every edit; MassiveSLICER ingests.
/// Multiple clients may be connected at once — events carry a client id so
/// object ids from different apps never collide.
///
/// The WebSocket upgrade is done by hand over a TcpListener (HttpListener's
/// WebSocket support is Windows-only); RFC 6455 framing comes from
/// <see cref="WebSocket.CreateFromStream"/>.
/// </summary>
internal sealed class MassiveBrainServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<int, WebSocket> _clients = new();
    private int _nextClientId;

    public bool IsRunning => _listener is not null;

    /// <summary>Client connected: (clientId, description e.g. "blender @ 127.0.0.1").</summary>
    public event Action<int, string>? ClientConnected;

    /// <summary>Client disconnected: (clientId, reason).</summary>
    public event Action<int, string>? ClientDisconnected;

    /// <summary>Added/updated objects pushed by a client (with geometry).</summary>
    public event Action<int, IReadOnlyList<PlasticityObject>>? ObjectsReceived;

    /// <summary>Object ids deleted in a client's document.</summary>
    public event Action<int, IReadOnlyList<int>>? ObjectsDeleted;

    /// <summary>Diagnostic messages for the app console.</summary>
    public event Action<string>? Log;

    /// <summary>Starts listening on <paramref name="host"/>:<paramref name="port"/>.</summary>
    public void Start(string host, int port)
    {
        Stop();

        var address = ResolveBindAddress(host);
        var listener = new TcpListener(address, port);
        listener.Start();

        _listener = listener;
        _cts      = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        Log?.Invoke($"server listening on {address}:{port}");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;

        foreach (var (id, ws) in _clients)
        {
            try { ws.Abort(); } catch { /* ignore */ }
            _clients.TryRemove(id, out _);
        }

        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    public int ClientCount => _clients.Count;

    public void Dispose() => Stop();

    private static IPAddress ResolveBindAddress(string host)
    {
        host = host.Trim();
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (host is "*" or "0.0.0.0" || host.Equals("any", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Any;
        return IPAddress.TryParse(host, out var ip) ? ip : IPAddress.Loopback;
    }

    // -- Accept / handshake --------------------------------------------------

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) Log?.Invoke($"accept error: {ex.Message}");
                return;
            }
            _ = Task.Run(() => ServeClientAsync(tcp, ct));
        }
    }

    private async Task ServeClientAsync(TcpClient tcp, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _nextClientId);
        string remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        WebSocket? ws = null;
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();

            if (!await TryUpgradeAsync(stream, ct))
            {
                Log?.Invoke($"client {remote}: not a WebSocket upgrade — dropped.");
                return;
            }

            ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer          = true,
                KeepAliveInterval = TimeSpan.FromSeconds(20),
            });
            _clients[id] = ws;
            ClientConnected?.Invoke(id, remote);

            await ReceiveLoopAsync(id, ws, ct);
        }
        catch (OperationCanceledException) { /* server stopping */ }
        catch (Exception ex)
        {
            ClientDisconnected?.Invoke(id, ex.Message);
        }
        finally
        {
            if (_clients.TryRemove(id, out _))
                ClientDisconnected?.Invoke(id, "closed");
            try { ws?.Dispose(); } catch { /* ignore */ }
            try { tcp.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>Reads the HTTP upgrade request and answers 101 Switching Protocols.</summary>
    private static async Task<bool> TryUpgradeAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        int len = 0;
        while (true)
        {
            int n = await stream.ReadAsync(buf.AsMemory(len, buf.Length - len), ct);
            if (n <= 0) return false;
            len += n;
            if (HeadersComplete(buf, len)) break;
            if (len >= buf.Length) return false;      // oversized request
        }

        var request = Encoding.ASCII.GetString(buf, 0, len);
        string? key = null;
        foreach (var line in request.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line.AsSpan(0, colon).Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                key = line[(colon + 1)..].Trim();
        }
        if (key is null) return false;

        string accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        var response = "HTTP/1.1 101 Switching Protocols\r\n"
                     + "Upgrade: websocket\r\n"
                     + "Connection: Upgrade\r\n"
                     + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        var respBytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(respBytes, ct);
        return true;

        static bool HeadersComplete(byte[] b, int n)
        {
            for (int i = 3; i < n; i++)
                if (b[i] == '\n' && b[i - 1] == '\r' && b[i - 2] == '\n' && b[i - 3] == '\r')
                    return true;
            return false;
        }
    }

    // -- Inbound ---------------------------------------------------------------

    private async Task ReceiveLoopAsync(int clientId, WebSocket ws, CancellationToken ct)
    {
        var frame = new byte[64 * 1024];
        using var ms = new MemoryStream();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(frame, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                    return;
                ms.Write(frame, 0, res.Count);
            }
            while (!res.EndOfMessage);

            if (ms.Length < 4) continue;
            try
            {
                Decode(clientId, ws, ms.GetBuffer(), (int)ms.Length, ct);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"client {clientId} decode error: {ex.Message}");
            }
        }
    }

    private void Decode(int clientId, WebSocket ws, byte[] d, int len, CancellationToken ct)
    {
        int off = 0;
        var type = (PlasticityMessageType)PlasticityWire.ReadU32(d, ref off, len);

        switch (type)
        {
            case PlasticityMessageType.Handshake:
            {
                // Optional trailing utf8 = client app name ("blender", "rhino", …).
                string appName = len > off ? Encoding.UTF8.GetString(d, off, len - off).Trim('\0', ' ') : "";
                Log?.Invoke($"client {clientId} handshake{(appName.Length > 0 ? $": {appName}" : "")}");
                // Ack so addons can confirm they reached MassiveBRAIN (not some other server).
                var ack = Encoding.UTF8.GetBytes("MASSIVEBRAIN");
                var reply = new byte[4 + ack.Length];
                BitConverter.TryWriteBytes(reply.AsSpan(0), (uint)PlasticityMessageType.Handshake);
                ack.CopyTo(reply, 4);
                _ = ws.SendAsync(reply, WebSocketMessageType.Binary, endOfMessage: true, ct);
                break;
            }

            case PlasticityMessageType.Transaction:
            {
                var added   = new List<PlasticityObject>();
                var deleted = new List<int>();
                PlasticityWire.DecodeTransaction(d, ref off, len, added, deleted);
                if (added.Count   > 0) ObjectsReceived?.Invoke(clientId, added);
                if (deleted.Count > 0) ObjectsDeleted?.Invoke(clientId, deleted);
                break;
            }

            default:
                Log?.Invoke($"client {clientId}: ignored message type {(uint)type}");
                break;
        }
    }
}
