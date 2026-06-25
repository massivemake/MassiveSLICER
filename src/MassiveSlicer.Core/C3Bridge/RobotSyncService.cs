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

    // -- Events ----------------------------------------------------------------

    /// <summary>Fired after a successful TCP connect.</summary>
    public event EventHandler? Connected;

    /// <summary>Fired when the socket closes (clean or error).</summary>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>Fired each time $AXIS_ACT is read. Array is [A1..A6] in KRL degrees.</summary>
    public event EventHandler<double[]>? AxesUpdated;

    /// <summary>Fired each time $POS_ACT is read.</summary>
    public event EventHandler<(double X, double Y, double Z, double A, double B, double C)>? TcpUpdated;

    /// <summary>Fired when live I/O polling reads a batch of KRL variables.</summary>
    public event EventHandler<IReadOnlyDictionary<string, string>>? IoSnapshotUpdated;

    private bool _ioPollingEnabled;
    private IReadOnlyList<string> _ioPollVariables = [];
    private int _streamTick;

    /// <summary>When true, the stream loop periodically batch-reads <see cref="SetIoPollVariables"/>.</summary>
    public bool IoPollingEnabled
    {
        get => _ioPollingEnabled;
        set => _ioPollingEnabled = value;
    }

    /// <summary>Configures KRL variables to poll when <see cref="IoPollingEnabled"/> is true.</summary>
    public void SetIoPollVariables(IReadOnlyList<string> variables) => _ioPollVariables = variables;

    // -- Lifecycle -------------------------------------------------------------

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

    // -- One-shot sync ---------------------------------------------------------

    /// <summary>Reads $AXIS_ACT and $POS_ACT once and fires the corresponding events.</summary>
    public async Task SyncOnceAsync(CancellationToken ct = default)
    {
        var axisStr = await _client.ReadAsync("$AXIS_ACT", 2000, ct);
        AxesUpdated?.Invoke(this, KrlVarParser.ParseAxisAct(axisStr));

        var posStr = await _client.ReadAsync("$POS_ACT", 2000, ct);
        TcpUpdated?.Invoke(this, KrlVarParser.ParsePosAct(posStr));
    }

    // -- Direct variable access (for the bed-calibration handshake) ------------
    // Call StopStreaming() first: the client allows only one request in flight, so
    // these must not run concurrently with the polling loop.

    /// <summary>Reads a KRL <c>$FLAG[idx]</c> boolean.</summary>
    public async Task<bool> ReadFlagAsync(int idx, CancellationToken ct = default)
    {
        var s = await _client.ReadAsync($"$FLAG[{idx}]", 2000, ct);
        return s.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sets a KRL <c>$FLAG[idx]</c> boolean.</summary>
    public Task SetFlagAsync(int idx, bool value, CancellationToken ct = default)
        => _client.WriteAsync($"$FLAG[{idx}]", value ? "TRUE" : "FALSE", 2000, ct);

    /// <summary>Reads <c>$AXIS_ACT</c> and returns the joint array [A1..A6, E1] in KRL degrees.</summary>
    public async Task<double[]> ReadAxesAsync(CancellationToken ct = default)
    {
        var s = await _client.ReadAsync("$AXIS_ACT", 2000, ct);
        return KrlVarParser.ParseAxisAct(s);
    }

    /// <summary>Sets a global KRL BOOL by name, e.g. a CELL() trigger flag (streaming must be paused).</summary>
    public Task SetBoolAsync(string name, bool value, CancellationToken ct = default)
        => _client.WriteAsync(name, value ? "TRUE" : "FALSE", 2000, ct);

    /// <summary>Writes a KRL variable by name (any type) and returns the controller's echoed value.
    /// For a FRAME such as <c>BASE_DATA[2]</c> pass an aggregate value, e.g.
    /// <c>{X 100.0, Y 0.0, Z 0.0, A 0.0, B 0.0, C 0.0}</c>.</summary>
    public Task<string> WriteVarAsync(string name, string value, CancellationToken ct = default)
        => _client.WriteAsync(name, value, 2000, ct);

    /// <summary>Reads a KRL variable by name and returns the raw controller value.</summary>
    public Task<string> ReadVarAsync(string name, CancellationToken ct = default)
        => _client.ReadAsync(name, 2000, ct);

    // ── MassiveSlicer motion command server (MASSIVE_SERVER.src) ───────────────
    // Drives motion by writing the MS_* globals over KUKAVARPROXY: set the target + params, then
    // bump MS_SEQ; the server runs it and echoes MS_SEQ to MS_ACK. No .src edits / reboots needed.
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    private int _msSeq = -1;

    /// <summary>Syncs the host command counter to the server's last ack (call after connect).</summary>
    public async Task<int> InitCommandServerAsync(CancellationToken ct = default)
    {
        var s = await _client.ReadAsync("MS_ACK", 2000, ct);
        _msSeq = int.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer, Inv, out var a) ? a : 0;
        return _msSeq;
    }

    /// <summary>PTP (linear=false) or LIN (linear=true) the scanner/tool to a Cartesian pose (mm, deg).
    /// Returns true when the server acks completion, false on timeout.</summary>
    public Task<bool> SendPoseAsync(bool linear, double x, double y, double z, double a, double b, double c,
        int vel, int tool, int baseIndex, int timeoutMs = 60000, CancellationToken ct = default)
    {
        string pose = $"{{X {F(x)}, Y {F(y)}, Z {F(z)}, A {F(a)}, B {F(b)}, C {F(c)}}}";
        return SendCommandAsync(linear ? 2 : 1, ("MS_POSE", pose), vel, tool, baseIndex, timeoutMs, ct);
    }

    /// <summary>PTP to a joint target (A1..A6, E1 in KRL degrees).</summary>
    public Task<bool> SendAxesAsync(double a1, double a2, double a3, double a4, double a5, double a6, double e1,
        int vel, int timeoutMs = 60000, CancellationToken ct = default)
    {
        string ax = $"{{A1 {F(a1)}, A2 {F(a2)}, A3 {F(a3)}, A4 {F(a4)}, A5 {F(a5)}, A6 {F(a6)}, E1 {F(e1)}}}";
        return SendCommandAsync(3, ("MS_AXIS", ax), vel, 0, 0, timeoutMs, ct);
    }

    /// <summary>Move to the controller's HOME position.</summary>
    public Task<bool> GoHomeAsync(int vel = 20, int timeoutMs = 60000, CancellationToken ct = default)
        => SendCommandAsync(4, null, vel, 0, 0, timeoutMs, ct);

    /// <summary>Tells the server loop to exit (CMD 99). Fire-and-forget.</summary>
    public Task StopCommandServerAsync(CancellationToken ct = default)
        => SendCommandAsync(99, null, 1, 0, 0, 5000, ct);

    private static string F(double v) => v.ToString("F3", Inv);

    private async Task<bool> SendCommandAsync(int cmd, (string Name, string Value)? target,
        int vel, int tool, int baseIndex, int timeoutMs, CancellationToken ct)
    {
        if (_msSeq < 0) await InitCommandServerAsync(ct);

        await _client.WriteAsync("MS_VEL", Math.Clamp(vel, 1, 100).ToString(Inv), 2000, ct);
        if (tool > 0)        await _client.WriteAsync("MS_TOOL", tool.ToString(Inv), 2000, ct);
        await _client.WriteAsync("MS_BASE", baseIndex.ToString(Inv), 2000, ct);
        if (target is { } t) await _client.WriteAsync(t.Name, t.Value, 2000, ct);
        await _client.WriteAsync("MS_CMD", cmd.ToString(Inv), 2000, ct);

        int seq = ++_msSeq;
        await _client.WriteAsync("MS_SEQ", seq.ToString(Inv), 2000, ct);   // trigger

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var s = await _client.ReadAsync("MS_ACK", 2000, ct);
            if (int.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer, Inv, out var ack) && ack == seq)
                return true;
            await Task.Delay(50, ct);
        }
        return false;
    }

    /// <summary>
    /// Selects and starts a KRL program by name via the C3 Bridge program-control command
    /// (no dispatcher needed). Streaming must be paused. e.g. "/R1/Program/BED_SCAN_CAL".
    /// </summary>
    public Task<C3BridgeClient.ProgramResult> RunProgramAsync(string programName, CancellationToken ct = default)
        => _client.RunProgramAsync(programName, force: true, 4000, ct);

    /// <summary>Selects a KRL program by name (force) without starting it. Streaming must be paused.</summary>
    public Task<C3BridgeClient.ProgramResult> SelectProgramAsync(string programName, CancellationToken ct = default)
        => _client.SelectProgramAsync(programName, force: true, 4000, ct);

    /// <summary>Interpreter control: Reset (1) / Start (2) / Stop (3) / Cancel (4). Streaming must be paused.</summary>
    public Task<C3BridgeClient.ProgramResult> ProgramControlAsync(byte command, ushort interpreter = 1, CancellationToken ct = default)
        => _client.ProgramControlAsync(command, interpreter, 4000, ct);

    // -- Continuous streaming --------------------------------------------------

    /// <summary>
    /// Begins a background polling loop that calls <see cref="SyncOnceAsync"/>
    /// every <paramref name="intervalMs"/> milliseconds.
    /// Safe to call multiple times -- only one loop runs at a time.
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
            catch { break; } // socket error -- _client fires Disconnected

            if (_ioPollingEnabled && _ioPollVariables.Count > 0 && ++_streamTick % 5 == 0)
            {
                try { await PollIoSnapshotAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch { /* keep axis stream alive */ }
            }

            try   { await Task.Delay(intervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task PollIoSnapshotAsync(CancellationToken ct)
    {
        var dict = new Dictionary<string, string>(_ioPollVariables.Count, StringComparer.Ordinal);
        foreach (var variable in _ioPollVariables)
        {
            try { dict[variable] = await _client.ReadAsync(variable, 1500, ct); }
            catch { dict[variable] = ""; }
        }
        IoSnapshotUpdated?.Invoke(this, dict);
    }

    // -- Dispose ---------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopStreaming();
        _client.Dispose();
    }
}
