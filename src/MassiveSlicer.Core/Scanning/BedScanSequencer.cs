using MassiveSlicer.Core.C3Bridge;

namespace MassiveSlicer.Core.Scanning;

/// <summary>Progress report emitted after each capture step.</summary>
public sealed class BedScanProgress
{
    public required int    Step       { get; init; }  // 0-based index, 0..ScanSteps-1
    public required int    TotalSteps { get; init; }
    public required double E1Deg      { get; init; }  // actual E1 read from $AXIS_ACT
    public required string Message    { get; init; }
}

/// <summary>One captured step: the actual E1 angle and the resulting point cloud.</summary>
public sealed class BedScanStep
{
    public required int               StepIndex { get; init; }
    public required double            E1Deg     { get; init; }
    public required ScanCaptureResult Capture   { get; init; }
}

/// <summary>
/// Orchestrates the LFAM3 rotary-bed auto-scan sequence.
///
/// Sends KUKAVARPROXY handshake commands to BedScan.src running on the KRC4,
/// reads the actual E1 angle at each step, and fires one Zivid capture per step.
///
/// Flow (9 total CMD=1 writes):
///   WriteAsync(CMD=1)  -- start
///   For each step 0..ScanSteps-1:
///     WaitFor(STATUS=2)       -- KRL reached position
///     ReadAsync($AXIS_ACT)    -- capture actual E1
///     ZividScanService.Capture()
///     WriteAsync(CMD=1)       -- advance (last write triggers STATUS=3)
///   WaitFor(STATUS=3)         -- sequence complete
/// </summary>
public sealed class BedScanSequencer
{
    private readonly C3BridgeClient _bridge;
    private readonly int            _scanSteps;
    private readonly string?        _saveDirectory;

    public BedScanSequencer(
        C3BridgeClient bridge,
        int            scanSteps     = 8,
        string?        saveDirectory = null)
    {
        _bridge        = bridge;
        _scanSteps     = scanSteps;
        _saveDirectory = saveDirectory;
    }

    /// <summary>
    /// Runs the full scan sequence. BedScan.src must already be running on the
    /// KRC4 and blocked on its initial WAIT FOR CMD==1.
    /// Returns one <see cref="BedScanStep"/> per step, in order.
    /// </summary>
    public async Task<IReadOnlyList<BedScanStep>> RunAsync(
        IProgress<BedScanProgress>? progress = null,
        CancellationToken           ct       = default)
    {
        var results = new List<BedScanStep>(_scanSteps);

        await _bridge.WriteAsync("BED_SCAN_CMD", "1", 2000, ct);

        for (int step = 0; step < _scanSteps; step++)
        {
            await WaitForStatusAsync(2, ct);

            var axisRaw       = await _bridge.ReadAsync("$AXIS_ACT", 2000, ct);
            var (_, e1)       = KrlVarParser.ParseAxisActWithE1(axisRaw);

            progress?.Report(new BedScanProgress
            {
                Step       = step,
                TotalSteps = _scanSteps,
                E1Deg      = e1,
                Message    = $"Capturing step {step + 1}/{_scanSteps} at E1 = {e1:F1}°",
            });

            var capture = await Task.Run(
                () => ZividScanService.Capture(_saveDirectory, null,
                    msg => progress?.Report(new BedScanProgress
                    {
                        Step       = step,
                        TotalSteps = _scanSteps,
                        E1Deg      = e1,
                        Message    = msg,
                    })),
                ct);

            results.Add(new BedScanStep { StepIndex = step, E1Deg = e1, Capture = capture });

            await _bridge.WriteAsync("BED_SCAN_CMD", "1", 2000, ct);
        }

        await WaitForStatusAsync(3, ct);

        return results;
    }

    /// <summary>
    /// Sends CMD=99 to abort the KRL program cleanly.
    /// Best-effort — does not throw if the bridge is disconnected.
    /// </summary>
    public async Task AbortAsync(CancellationToken ct = default)
    {
        try { await _bridge.WriteAsync("BED_SCAN_CMD", "99", 2000, ct); }
        catch { }
    }

    private async Task WaitForStatusAsync(int expected, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await _bridge.ReadAsync("BED_SCAN_STATUS", 2000, ct);
            if (int.TryParse(raw.Trim(), out int status) && status == expected)
                return;
            await Task.Delay(50, ct);
        }
    }
}
