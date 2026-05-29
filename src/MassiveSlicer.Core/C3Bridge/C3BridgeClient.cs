using System.Net.Sockets;
using System.Text;

namespace MassiveSlicer.Core.C3Bridge;

/// <summary>
/// Async TCP client for the KUKAVARPROXY / C3Bridge protocol (port 7000).
///
/// Send frame  : [msgIdH, msgIdL, payloadLenH, payloadLenL, flag=0, nameLenH, nameLenL, ...nameASCII]
/// Receive frame: [msgIdH, msgIdL, msgLenH, msgLenL, flag, valLenH, valLenL, ...valueUTF8]
///
/// Only one read can be in-flight at a time; a second concurrent call throws.
/// </summary>
public sealed class C3BridgeClient : IDisposable
{
    private TcpClient?      _tcp;
    private NetworkStream?  _stream;
    private CancellationTokenSource? _readCts;
    private int             _msgId;
    private bool            _disposed;

    private readonly object                       _lock    = new();
    private TaskCompletionSource<string>?         _pending;

    public bool IsConnected { get; private set; }

    /// <summary>Fired on the thread-pool when the socket closes or errors.</summary>
    public event EventHandler<Exception?>? Disconnected;

    // -- Connection ------------------------------------------------------------

    public async Task ConnectAsync(
        string host, int port = 7000,
        int timeoutMs = 3000,
        CancellationToken ct = default)
    {
        if (IsConnected) return;

        _tcp = new TcpClient { NoDelay = true };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            await _tcp.ConnectAsync(host, port, cts.Token);
        }
        catch
        {
            _tcp.Dispose();
            _tcp = null;
            throw;
        }

        _stream     = _tcp.GetStream();
        IsConnected = true;
        _readCts    = new CancellationTokenSource();

        _ = Task.Run(() => ReadLoopAsync(_readCts.Token));
    }

    public void Disconnect() => Cleanup(null);

    private void Cleanup(Exception? reason)
    {
        if (!IsConnected) return;
        IsConnected = false;

        _readCts?.Cancel();

        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose();   } catch { }
        _stream = null;
        _tcp    = null;

        TaskCompletionSource<string>? pending;
        lock (_lock) { pending = _pending; _pending = null; }
        pending?.TrySetException(reason ?? new IOException("C3Bridge: disconnected"));

        Disconnected?.Invoke(this, reason);
    }

    // -- Read ------------------------------------------------------------------

    /// <summary>
    /// Reads a KRL variable, e.g. <c>$AXIS_ACT</c> or <c>$POS_ACT</c>.
    /// Returns the raw UTF-8 value string from the KRC4.
    /// </summary>
    public async Task<string> ReadAsync(
        string varName,
        int timeoutMs = 2000,
        CancellationToken ct = default)
    {
        if (!IsConnected || _stream is null)
            throw new InvalidOperationException("C3Bridge: not connected");

        var nameBytes  = Encoding.ASCII.GetBytes(varName);
        int payloadLen = 3 + nameBytes.Length;
        int msgId      = Interlocked.Increment(ref _msgId) & 0xFFFF;

        // Build the request frame.
        var msg = new byte[4 + payloadLen];
        msg[0] = (byte)(msgId >> 8);
        msg[1] = (byte)(msgId & 0xFF);
        msg[2] = (byte)(payloadLen >> 8);
        msg[3] = (byte)(payloadLen & 0xFF);
        msg[4] = 0;                                    // read flag
        msg[5] = (byte)(nameBytes.Length >> 8);
        msg[6] = (byte)(nameBytes.Length & 0xFF);
        nameBytes.CopyTo(msg, 7);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            if (_pending is not null)
                throw new InvalidOperationException("C3Bridge: a read is already in progress");
            _pending = tcs;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        await using var _ = timeoutCts.Token.Register(() =>
        {
            lock (_lock) { if (_pending == tcs) _pending = null; }
            tcs.TrySetException(new TimeoutException($"C3Bridge: timeout reading \"{varName}\""));
        });

        await _stream.WriteAsync(msg, ct);
        return await tcs.Task;
    }

    // -- Read loop -------------------------------------------------------------

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        // Header: 7 bytes fixed; body: valLen bytes extracted from header[5..6].
        var  header    = new byte[7];
        int  headerPos = 0;
        byte[]? body   = null;
        int  bodyPos   = 0;
        var  buf       = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream!.ReadAsync(buf, 0, buf.Length, ct);
                if (n == 0) break;

                int offset = 0;
                while (offset < n)
                {
                    if (body is null)
                    {
                        int take = Math.Min(7 - headerPos, n - offset);
                        Buffer.BlockCopy(buf, offset, header, headerPos, take);
                        headerPos += take;
                        offset    += take;

                        if (headerPos == 7)
                        {
                            int valLen = (header[5] << 8) | header[6];
                            body    = new byte[valLen];
                            bodyPos = 0;

                            // valLen == 0 means empty value -- resolve immediately.
                            if (valLen == 0) goto resolve;
                        }
                    }
                    else
                    {
                        int take = Math.Min(body.Length - bodyPos, n - offset);
                        Buffer.BlockCopy(buf, offset, body, bodyPos, take);
                        bodyPos += take;
                        offset  += take;

                        if (bodyPos == body.Length) goto resolve;
                    }

                    continue;
                    resolve:
                    var value = Encoding.UTF8.GetString(body ?? []);
                    TaskCompletionSource<string>? pending;
                    lock (_lock) { pending = _pending; _pending = null; }
                    pending?.TrySetResult(value);

                    headerPos = 0;
                    body      = null;
                    bodyPos   = 0;
                }
            }
        }
        catch (OperationCanceledException) { } // normal on Disconnect/Dispose
        catch (Exception ex) { Cleanup(ex); return; }

        Cleanup(null);
    }

    // -- Dispose ---------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup(null);
    }
}
