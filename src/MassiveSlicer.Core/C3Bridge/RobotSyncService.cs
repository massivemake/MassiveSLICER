namespace MassiveSlicer.Core.C3Bridge;

/// <summary>
/// High-level robot sync service over C3Bridge.
/// Manages the TCP connection lifecycle and drives continuous joint/TCP polling.
/// All events are raised on the thread-pool; marshal to the UI thread as needed.
/// </summary>
public sealed class RobotSyncService : IDisposable
{
    private readonly C3BridgeClient _client = new();
    private CancellationTokenSource? _streamCts;
    private bool _disposed;

    public bool IsConnected => _client.IsConnected;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fired after a successful TCP connect.</summary>
    public event EventHandler? Connected;

    /// <summary>Fired when the socket closes (clean or error).</summary>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>Fired each time $AXIS_ACT is read. Array is [A1..A6] in KRL degrees.</summary>
    public event EventHandler<double[]>? AxesUpdated;

    /// <summary>Fired each time $POS_ACT is read.</summary>
    public event EventHandler<(double X, double Y, double Z, double A, double B, double C)>? TcpUpdated;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public RobotSyncService()
    {
        _client.Disconnected += (_, ex) =>
        {
            StopStreaming();
            Disconnected?.Invoke(this, ex);
        };
    }

    /// <summary>
    /// Opens the TCP connection. Throws on timeout or unreachable host.
    /// Call <see cref="StartStreaming"/> afterward to begin continuous polling.
    /// </summary>
    public async Task ConnectAsync(
        string host, int port = 7000,
        int timeoutMs = 3000,
        CancellationToken ct = default)
    {
        await _client.ConnectAsync(host, port, timeoutMs, ct);
        Connected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops streaming and closes the connection.</summary>
    public void Disconnect()
    {
        StopStreaming();
        _client.Disconnect();
    }

    // ── One-shot sync ─────────────────────────────────────────────────────────

    /// <summary>Reads $AXIS_ACT and $POS_ACT once and fires the corresponding events.</summary>
    public async Task SyncOnceAsync(CancellationToken ct = default)
    {
        var axisStr = await _client.ReadAsync("$AXIS_ACT", 2000, ct);
        AxesUpdated?.Invoke(this, KrlVarParser.ParseAxisAct(axisStr));

        var posStr = await _client.ReadAsync("$POS_ACT", 2000, ct);
        TcpUpdated?.Invoke(this, KrlVarParser.ParsePosAct(posStr));
    }

    // ── Continuous streaming ──────────────────────────────────────────────────

    /// <summary>
    /// Begins a background polling loop that calls <see cref="SyncOnceAsync"/>
    /// every <paramref name="intervalMs"/> milliseconds.
    /// Safe to call multiple times — only one loop runs at a time.
    /// </summary>
    public void StartStreaming(int intervalMs = 100)
    {
        if (_streamCts is not null) return;
        _streamCts = new CancellationTokenSource();
        _ = Task.Run(() => StreamLoopAsync(intervalMs, _streamCts.Token));
    }

    /// <summary>Stops the polling loop. Does not close the TCP connection.</summary>
    public void StopStreaming()
    {
        _streamCts?.Cancel();
        _streamCts = null;
    }

    private async Task StreamLoopAsync(int intervalMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _client.IsConnected)
        {
            try   { await SyncOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { break; } // socket error — _client fires Disconnected

            try   { await Task.Delay(intervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopStreaming();
        _client.Dispose();
    }
}
