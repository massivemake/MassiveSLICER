using System.Net.Sockets;
using System.Text;

namespace MassiveSlicer.Core.C3Bridge;

/// <summary>
/// Async TCP client for the C3 Bridge Interface Server (ulsu-tech) / KUKAVARPROXY
/// protocol (port 7000).
///
/// Every message: [tagId(2, BE)][msgLen(2, BE)][payload...]  where msgLen counts the
/// payload bytes from offset 4 onward, and payload[0] is the message type:
///   0  = read variable   (ASCII, name length-prefixed)
///   1  = write variable   (ASCII)
///   10 = program control  (reset/start/stop/cancel, or select/run)
///
/// Multibyte integer fields are big-endian; UTF-16 program-name strings are little-endian.
/// Only one request may be in flight at a time; a second concurrent call throws.
/// </summary>
public sealed class C3BridgeClient : IDisposable
{
    private TcpClient?      _tcp;
    private NetworkStream?  _stream;
    private CancellationTokenSource? _readCts;
    private int             _msgId;
    private bool            _disposed;

    private readonly object               _lock = new();
    private TaskCompletionSource<byte[]>? _pending;   // resolved with the response payload (from offset 4)

    public bool IsConnected { get; private set; }

    /// <summary>Result of a program-control request.</summary>
    public readonly record struct ProgramResult(bool Success, int ErrorCode);

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

        TaskCompletionSource<byte[]>? pending;
        lock (_lock) { pending = _pending; _pending = null; }
        pending?.TrySetException(reason ?? new IOException("C3Bridge: disconnected"));

        Disconnected?.Invoke(this, reason);
    }

    // -- Core request ----------------------------------------------------------

    /// <summary>
    /// Sends one request whose <paramref name="payload"/> is the bytes from offset 4 onward
    /// (payload[0] = message type). Returns the response payload (also from offset 4).
    /// </summary>
    private async Task<byte[]> SendRequestAsync(byte[] payload, int timeoutMs, CancellationToken ct)
    {
        if (!IsConnected || _stream is null)
            throw new InvalidOperationException("C3Bridge: not connected");

        int msgId = Interlocked.Increment(ref _msgId) & 0xFFFF;
        var msg   = new byte[4 + payload.Length];
        msg[0] = (byte)(msgId >> 8);
        msg[1] = (byte)(msgId & 0xFF);
        msg[2] = (byte)(payload.Length >> 8);
        msg[3] = (byte)(payload.Length & 0xFF);
        payload.CopyTo(msg, 4);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_pending is not null)
                throw new InvalidOperationException("C3Bridge: an operation is already in progress");
            _pending = tcs;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        await using var _ = timeoutCts.Token.Register(() =>
        {
            lock (_lock) { if (_pending == tcs) _pending = null; }
            tcs.TrySetException(new TimeoutException("C3Bridge: request timed out"));
        });

        await _stream.WriteAsync(msg, ct);
        return await tcs.Task;
    }

    // -- Read / write variables ------------------------------------------------

    /// <summary>Reads a KRL variable, e.g. <c>$AXIS_ACT</c>. Returns the raw value string.</summary>
    public async Task<string> ReadAsync(string varName, int timeoutMs = 2000, CancellationToken ct = default)
    {
        var name = Encoding.ASCII.GetBytes(varName);
        var payload = new byte[3 + name.Length];
        payload[0] = 0;                              // type: read
        payload[1] = (byte)(name.Length >> 8);
        payload[2] = (byte)(name.Length & 0xFF);
        name.CopyTo(payload, 3);

        var resp = await SendRequestAsync(payload, timeoutMs, ct);
        return ParseVarValue(resp);
    }

    /// <summary>Writes a KRL variable, e.g. <c>BED_SCAN_CMD</c>. Returns the echoed value.</summary>
    public async Task<string> WriteAsync(string varName, string value, int timeoutMs = 2000, CancellationToken ct = default)
    {
        var name = Encoding.ASCII.GetBytes(varName);
        var val  = Encoding.ASCII.GetBytes(value);
        var payload = new byte[1 + 2 + name.Length + 2 + val.Length];
        int i = 0;
        payload[i++] = 1;                            // type: write
        payload[i++] = (byte)(name.Length >> 8);
        payload[i++] = (byte)(name.Length & 0xFF);
        name.CopyTo(payload, i); i += name.Length;
        payload[i++] = (byte)(val.Length >> 8);
        payload[i++] = (byte)(val.Length & 0xFF);
        val.CopyTo(payload, i);

        var resp = await SendRequestAsync(payload, timeoutMs, ct);
        return ParseVarValue(resp);
    }

    // Var read/write response payload: [type(1)][valLen(2, BE)][value(ASCII)].
    private static string ParseVarValue(byte[] payload)
    {
        if (payload.Length < 3) return "";
        int valLen = (payload[1] << 8) | payload[2];
        int avail  = Math.Min(valLen, payload.Length - 3);
        return avail <= 0 ? "" : Encoding.UTF8.GetString(payload, 3, avail);
    }

    // -- Program control (message type 10) -------------------------------------

    /// <summary>Selects (cmd 5) or runs (cmd 6) a KRL program by name. UTF-16LE name, char-count length.</summary>
    private async Task<ProgramResult> ProgramSelectRunAsync(
        byte command, string programName, bool force, int timeoutMs, CancellationToken ct)
    {
        var name   = Encoding.Unicode.GetBytes(programName);  // UTF-16LE
        int nameCh = programName.Length;
        var payload = new byte[1 + 1 + 2 + 2 + name.Length + 2 + 0 + 1];
        int i = 0;
        payload[i++] = 10;                           // type: program control
        payload[i++] = command;                      // 5 = Select, 6 = Run
        payload[i++] = 0; payload[i++] = 0;          // Interpreter Type (unused for select/run)
        payload[i++] = (byte)(nameCh >> 8);          // program name length (chars), BE
        payload[i++] = (byte)(nameCh & 0xFF);
        name.CopyTo(payload, i); i += name.Length;
        payload[i++] = 0; payload[i++] = 0;          // parameters length (chars) = 0
        payload[i++] = (byte)(force ? 1 : 0);        // force select/run

        var resp = await SendRequestAsync(payload, timeoutMs, ct);
        return ParseProgramResult(resp);
    }

    /// <summary>Runs (selects + starts) a KRL program by name (e.g. "/R1/BED_SCAN_CAL").</summary>
    public Task<ProgramResult> RunProgramAsync(string programName, bool force = true, int timeoutMs = 4000, CancellationToken ct = default)
        => ProgramSelectRunAsync(6, programName, force, timeoutMs, ct);

    /// <summary>Selects a KRL program by name without starting it.</summary>
    public Task<ProgramResult> SelectProgramAsync(string programName, bool force = true, int timeoutMs = 4000, CancellationToken ct = default)
        => ProgramSelectRunAsync(5, programName, force, timeoutMs, ct);

    /// <summary>Reset (1) / Start (2) / Stop (3) / Cancel (4) the interpreter (0 = Submit, 1 = Robot).</summary>
    public async Task<ProgramResult> ProgramControlAsync(byte command, ushort interpreter = 1, int timeoutMs = 4000, CancellationToken ct = default)
    {
        var payload = new byte[1 + 1 + 2];
        payload[0] = 10;
        payload[1] = command;                        // 1=Reset, 2=Start, 3=Stop, 4=Cancel
        payload[2] = (byte)(interpreter >> 8);
        payload[3] = (byte)(interpreter & 0xFF);

        var resp = await SendRequestAsync(payload, timeoutMs, ct);
        return ParseProgramResult(resp);
    }

    // Program-control response payload: [type(10)][cmd][errCode(2, BE)][success(1)].
    private static ProgramResult ParseProgramResult(byte[] payload)
    {
        if (payload.Length < 5) return new ProgramResult(false, -1);
        int err     = (payload[2] << 8) | payload[3];
        bool success = payload[4] != 0;
        return new ProgramResult(success, err);
    }

    // -- Read loop (length-based) ----------------------------------------------

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var    header    = new byte[4];   // tagId(2) + msgLen(2)
        int    headerPos = 0;
        byte[]? body     = null;
        int    bodyPos   = 0;
        var    buf       = new byte[8192];

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
                        int take = Math.Min(4 - headerPos, n - offset);
                        Buffer.BlockCopy(buf, offset, header, headerPos, take);
                        headerPos += take;
                        offset    += take;

                        if (headerPos == 4)
                        {
                            int msgLen = (header[2] << 8) | header[3];
                            body    = new byte[msgLen];
                            bodyPos = 0;
                            if (msgLen == 0) goto resolve;
                        }
                        continue;
                    }

                    int taken = Math.Min(body.Length - bodyPos, n - offset);
                    Buffer.BlockCopy(buf, offset, body, bodyPos, taken);
                    bodyPos += taken;
                    offset  += taken;

                    if (bodyPos < body.Length) continue;

                    resolve:
                    var payload = body ?? [];
                    TaskCompletionSource<byte[]>? pending;
                    lock (_lock) { pending = _pending; _pending = null; }
                    pending?.TrySetResult(payload);

                    headerPos = 0;
                    body      = null;
                    bodyPos   = 0;
                }
            }
        }
        catch (OperationCanceledException) { }       // normal on Disconnect/Dispose
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
