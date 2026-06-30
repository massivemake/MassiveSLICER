using Avalonia.Threading;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.FK;
using OpenTK.Mathematics;
using NVec3 = System.Numerics.Vector3;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace MassiveSlicer.App.Views;

public partial class ViewportView
{
    readonly DispatcherTimer _seqTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly System.Diagnostics.Stopwatch _seqStopwatch = new();
    bool _seqTimerWired;
    List<SequenceWaypointTag>? _pendingSeqTags;
    bool _seqTagsPostScheduled;

    ToolChangeSequence? _seqData;
    ToolChangeSequencePath? _seqPath;
    string? _seqActiveId;
    float _seqProgress;
    double _seqPlaybackOffsetSeconds;
    bool _seqPlaying;
    string? _seqMountedToolBefore;
    string? _seqVisualMounted;
    int _selectedSeqWaypointIndex = -1;

    const double SeqPlaySeconds = 8.0;

    void WireToolChangeSequence(ViewportViewModel vm)
    {
        vm.OnSimulateToolChangeRequested = SimulateToolChangeSequence;
        vm.OnToggleToolChangePlaybackRequested = ToggleToolChangePlayback;
        vm.OnCollapseToolChangePlaybackRequested = CollapseToolChangePlayback;
        vm.OnToolChangeScrubRequested = ScrubToolChangeSequence;
        vm.SequenceWaypointEditor.OnWaypointIndexChanged = OnSequenceWaypointSelectionChanged;
        vm.SequenceWaypointEditor.OnSequenceReloadRequested = ReloadToolChangeSequence;
        if (_seqTimerWired) return;
        _seqTimerWired = true;
        _seqTimer.Tick += (_, _) => TickToolChangeSequence();
    }

    void CollapseToolChangePlayback()
    {
        // Closing the Pick/Deposit menu tears down the whole sequence: hides the playback
        // strip AND removes the associated tool-change path overlay from the viewport
        // (clears markers/tags, restores the prior mounted tool, deactivates the pills).
        ClearToolChangeSequence();
    }

    void ToggleToolChangePlayback()
    {
        if (_seqPath is null) return;
        _seqPlaying = !_seqPlaying;
        if (_seqPlaying)
        {
            if (_seqProgress >= 1f)
            {
                _seqProgress = 0f;
                _seqPlaybackOffsetSeconds = 0;
                ApplySequenceProgress(0f);
            }
            else
                _seqPlaybackOffsetSeconds = _seqProgress * SeqPlaySeconds;
            _seqStopwatch.Restart();
            _seqTimer.Start();
        }
        else
            _seqTimer.Stop();
        UpdateToolChangePlaybackUi();
    }

    void ScrubToolChangeSequence(int scrubValue)
    {
        if (_seqPath is null) return;
        _seqPlaying = false;
        _seqTimer.Stop();
        _seqProgress = Math.Clamp(scrubValue / 1000f, 0f, 1f);
        _seqPlaybackOffsetSeconds = _seqProgress * SeqPlaySeconds;
        _seqStopwatch.Restart();
        ApplySequenceProgress(_seqProgress);
        UpdateToolChangePlaybackUi();
    }

    void UpdateToolChangePlaybackUi()
    {
        if (_vm is null) return;
        _vm.SetToolChangeScrubFromViewport((int)Math.Round(_seqProgress * 1000));
        _vm.ToolChangeIsPlaying = _seqPlaying;
    }

    void SimulateToolChangeSequence(string sequenceId)
    {
        if (_seqActiveId == sequenceId)
        {
            if (_vm is { IsToolChangePlaybackExpanded: false })
            {
                _vm.IsToolChangePlaybackExpanded = true;
                return;
            }

            ClearToolChangeSequence();
            return;
        }

        if (_renderer.SelectedNode is { } sel && _renderer.IsToolpathNode(sel))
        {
            _renderer.Select(null);
            UpdateFocusOverlay();
        }

        if (!TryLoadToolChangeSequence(sequenceId, clearFirst: true))
            return;

        _seqProgress = 0f;
        _seqPlaybackOffsetSeconds = 0;
        _seqPlaying = true;
        _seqStopwatch.Restart();
        _seqTimer.Start();
        ApplySequenceProgress(0f);
        UpdateToolChangePlaybackUi();
        System.Console.WriteLine(
            $"[seq] playing {sequenceId}: {_seqPath!.DensePoints.Count} pts, " +
            $"{_seqPath.TotalLength:F0} mm, krc={KrlToolChangeSequenceParser.ResolveKrcRoot()}");
    }

    bool TryLoadToolChangeSequence(string sequenceId, bool clearFirst)
    {
        try
        {
            var data = KrlToolChangeSequenceParser.Parse(sequenceId);
            var path = ToolChangeSequencePathBuilder.Build(data, JointToRobrootMm);
            if (path is null || path.DensePoints.Count < 2)
            {
                System.Console.Error.WriteLine(
                    $"[seq] not enough resolvable waypoints for {sequenceId} " +
                    $"(parsed {data.Waypoints.Count}, krc={KrlToolChangeSequenceParser.ResolveKrcRoot()})");
                return false;
            }

            if (clearFirst)
            {
                ClearToolChangeSequence();
                _seqMountedToolBefore = _vm?.MountedToolName;
                _seqVisualMounted = _seqMountedToolBefore;
            }

            _seqData = data;
            _seqPath = path;
            _seqActiveId = sequenceId;
            if (_vm is not null)
                _vm.ActiveToolChangeSequenceId = sequenceId;
            return true;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine(
                $"[seq] failed to load {sequenceId}: {ex.Message} " +
                $"(krc={KrlToolChangeSequenceParser.ResolveKrcRoot()})");
            return false;
        }
    }

    void TickToolChangeSequence()
    {
        if (!_seqPlaying || _seqPath is null) { _seqTimer.Stop(); return; }

        var elapsed = _seqPlaybackOffsetSeconds + _seqStopwatch.Elapsed.TotalSeconds;
        _seqProgress = (float)Math.Clamp(elapsed / SeqPlaySeconds, 0, 1);
        ApplySequenceProgress(_seqProgress);

        if (_seqProgress < 1f)
        {
            UpdateToolChangePlaybackUi();
            return;
        }
        _seqPlaying = false;
        _seqTimer.Stop();
        ApplySequenceToolState(final: true);
        UpdateToolChangePlaybackUi();
        _vm?.RaiseToolChangeCommandsCanExecuteChanged();
    }

    void ApplySequenceProgress(float t)
    {
        if (_seqPath is null) return;

        var pos = t * _seqPath.TotalLength;
        var denseIdx = DenseIndexAtPosition(pos);
        var marker = SamplePathPosition(pos);
        UpdateSequenceOverlay(t, marker);
        UpdateToolChangeStepText(denseIdx);
        ApplySequenceToolState(final: false);
        GlCanvas.RequestNextFrameRendering();
    }

    int DenseIndexAtPosition(float pos)
    {
        var cum = _seqPath!.CumulativeLength;
        int idx = 0;
        while (idx < cum.Count - 1 && cum[idx + 1] <= pos) idx++;
        return idx;
    }

    void UpdateToolChangeStepText(int denseIdx)
    {
        if (_vm is null || _seqData is null || _seqPath is null) return;

        var wpAt = _seqPath.WaypointAtDenseIndex;
        int wi = 0;
        for (int i = 0; i < wpAt.Count; i++)
            if (denseIdx >= wpAt[i]) wi = i;

        var waypoints = _seqData.Waypoints;
        if (wi >= waypoints.Count) return;
        var wp = waypoints[wi];

        var acts = new List<string>();
        foreach (var o in wp.Outputs)
            acts.Add($"OUT{o.Index}={(o.State ? "ON" : "OFF")}");
        foreach (var w in wp.Waits)
            acts.Add(w.Type == "time" && w.Seconds.HasValue
                ? $"wait {w.Seconds.Value:g}s"
                : "wait signal");
        if (!string.IsNullOrEmpty(wp.ToolType))
            acts.Add($"tool=\"{wp.ToolType}\"");

        var move = wp.Move == KrlMoveKind.Lin ? "LIN" : "PTP";
        var actsStr = acts.Count > 0 ? $" · {string.Join(" · ", acts)}" : "";
        var denom = Math.Max(waypoints.Count - 1, 1);
        _vm.ToolChangeStepText = $"{wi}/{denom} {move} → {wp.Name}{actsStr}";
        _vm.ToolChangeStepTextCompact = $"{wi}/{denom} {move} → {wp.Name}";
    }

    TkVector3 SamplePathPosition(float pos)
    {
        var dense = _seqPath!.DensePoints;
        var cum   = _seqPath.CumulativeLength;
        int idx = 0;
        while (idx < cum.Count - 1 && cum[idx + 1] <= pos) idx++;
        if (idx >= dense.Count - 1) return ToTk(dense[^1]);
        var segLen = cum[idx + 1] - cum[idx];
        var f = segLen > 0f ? (pos - cum[idx]) / segLen : 0f;
        var a = ToTk(dense[idx]);
        var b = ToTk(dense[idx + 1]);
        return TkVector3.Lerp(a, b, f);
    }

    void UpdateSequenceOverlay(float progress, TkVector3 markerRobroot)
    {
        if (_seqPath is null) return;
        var offset = _robrootWorldPos;
        var waypointWorld = _seqPath.ResolvedWaypoints
            .Select(wp => ToTk(wp.Position) + offset)
            .ToList();
        _renderer.SetSequencePathOverlay(
            active: true,
            denseRobroot: _seqPath.DensePoints.Select(ToTk).ToList(),
            robrootOffset: offset,
            cum: _seqPath.CumulativeLength,
            segMove: _seqPath.SegmentMoves,
            progress: progress,
            markerRobroot: markerRobroot,
            waypointWorld: waypointWorld,
            selectedWaypointIndex: _selectedSeqWaypointIndex);
    }

    void OnSequenceWaypointSelectionChanged(int index)
    {
        _selectedSeqWaypointIndex = index;
        _renderer.SetSequenceWaypointSelection(index);
        if (_seqPath is not null && _seqProgress >= 0f)
            UpdateSequenceOverlay(_seqProgress, SamplePathPosition(_seqProgress * _seqPath.TotalLength));
        GlCanvas.RequestNextFrameRendering();
    }

    internal bool TryPickSequenceWaypoint(float mx, float my, float vpW, float vpH)
    {
        if (_vm is null || !_vm.IsDevMode || _seqPath is null || string.IsNullOrEmpty(_seqActiveId))
            return false;

        int hit = _renderer.PickSequenceWaypoint(mx, my, vpW, vpH);
        if (hit < 0 || hit >= _seqPath.ResolvedWaypoints.Count)
            return false;

        var resolved = _seqPath.ResolvedWaypoints[hit];
        _vm.SequenceWaypointEditor.Open(resolved.Waypoint, resolved.Index, _seqActiveId);
        return true;
    }

    void ReloadToolChangeSequence(string sequenceId)
    {
        if (string.IsNullOrEmpty(sequenceId)) return;
        var progress = _seqProgress;
        var wasPlaying = _seqPlaying;
        var offset = _seqPlaybackOffsetSeconds;
        if (!TryLoadToolChangeSequence(sequenceId, clearFirst: _seqActiveId != sequenceId))
            return;

        _seqProgress = progress;
        _seqPlaybackOffsetSeconds = offset;
        _seqPlaying = wasPlaying;
        if (wasPlaying)
        {
            _seqStopwatch.Restart();
            _seqTimer.Start();
        }
        else
            _seqTimer.Stop();

        ApplySequenceProgress(_seqProgress);
        UpdateToolChangePlaybackUi();
        GlCanvas.RequestNextFrameRendering();
    }

    void PostSequenceWaypointTags(IReadOnlyList<SequenceWaypointTag>? tags)
    {
        _pendingSeqTags = tags is null ? null : [.. tags];
        if (_seqTagsPostScheduled) return;
        _seqTagsPostScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _seqTagsPostScheduled = false;
            if (_vm is null) return;
            var pending = _pendingSeqTags;
            if (pending is null || pending.Count == 0)
                _vm.ClearSequenceWaypointTags();
            else
                _vm.SetSequenceWaypointTags(pending);
        });
    }

    internal void UpdateSequenceWaypointTags(int vpW, int vpH)
    {
        if (_vm is null || _seqPath is null || string.IsNullOrEmpty(_seqActiveId))
        {
            PostSequenceWaypointTags(null);
            return;
        }

        var offset = _robrootWorldPos;
        var resolved = _seqPath.ResolvedWaypoints;
        if (resolved.Count == 0)
        {
            PostSequenceWaypointTags(null);
            return;
        }

        static string PosKey(TkVector3 v) =>
            $"{MathF.Round(v.X)},{MathF.Round(v.Y)},{MathF.Round(v.Z)}";

        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < resolved.Count; i++)
        {
            var world = ToTk(resolved[i].Position) + offset;
            var key = PosKey(world);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }
            list.Add(i);
        }

        var tags = new List<SequenceWaypointTag>(resolved.Count);
        for (int i = 0; i < resolved.Count; i++)
        {
            var rw = resolved[i];
            var world = ToTk(rw.Position) + offset;
            var grp = groups[PosKey(world)];
            int gi = grp.IndexOf(i);
            int n = grp.Count;
            float dx = n > 1 ? (gi - (n - 1) * 0.5f) * 180f : 0f;
            var labelWorld = world + new TkVector3(dx, 0f, 110f);
            var screen = _renderer.ProjectToScreen(labelWorld, vpW, vpH);
            if (float.IsNaN(screen.X)) continue;

            var wp = rw.Waypoint;
            var label = wp.Name.Equals("HOME", StringComparison.OrdinalIgnoreCase)
                ? "H"
                : rw.Index.ToString();
            tags.Add(new SequenceWaypointTag(screen.X - 32f, screen.Y - 16f, label));
        }

        PostSequenceWaypointTags(tags);
    }

    void ApplySequenceToolState(bool final)
    {
        if (_seqPath?.ToolEvent is not { } te || _vm is null || _multiTools is null) return;

        bool onFlange = te.Attach ? _seqProgress >= te.Fraction : _seqProgress < te.Fraction;
        if (final)
            onFlange = te.Attach;

        var target = onFlange ? te.CellToolName : "";
        if (target == _seqVisualMounted) return;
        _seqVisualMounted = target;

        if (onFlange)
            MountToolByName(te.CellToolName);
        else
            ApplyMultiToolUnmount(_vm);
    }

    void MountToolByName(string toolName)
    {
        if (_vm?.ActiveCell is null) return;
        var cfg = _vm.ActiveCell.EffectiveTools.FirstOrDefault(t => t.Name == toolName);
        if (cfg is not null)
            ApplyMultiToolMount(cfg, _vm);
    }

    void ApplyMultiToolUnmountVisuals()
    {
        if (_multiTools is null) return;

        _multiTools.MountedToolName = null;
        foreach (var pair in _multiTools.Tools.Values)
        {
            pair.FlangeHolder.Visible = false;
            if (pair.DockHolder is { } dock)
            {
                dock.Visible = true;
                EnqueueCellGpuUpload(dock);
            }
        }

        _cellGpuUploadPending = _cellGpuUploadQueue.Count > 0 || _cellGpuUploadPending;
        _currentToolNode = null;
        RefreshMultiToolSelectability();
    }

    void ApplyMultiToolUnmount(ViewportViewModel vm, bool updateVm = true)
    {
        ApplyMultiToolUnmountVisuals();
        if (!updateVm) return;

        PostMultiToolVmState(vm, mountedToolName: "");
    }

    void PostMultiToolVmState(ViewportViewModel vm, string mountedToolName)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            vm.MountedToolName = mountedToolName;
            vm.SetActiveToolheadOutliner(string.IsNullOrEmpty(mountedToolName) ? null : mountedToolName);
            vm.RaiseToolChangeCommandsCanExecuteChanged();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            vm.MountedToolName = mountedToolName;
            vm.SetActiveToolheadOutliner(string.IsNullOrEmpty(mountedToolName) ? null : mountedToolName);
            vm.RaiseToolChangeCommandsCanExecuteChanged();
        });
    }

    void ClearToolChangeSequence(bool restorePriorMount = true)
    {
        _seqTimer.Stop();
        _seqPlaying = false;
        _seqActiveId = null;
        _seqData = null;
        _seqPath = null;
        _seqProgress = 0f;
        _seqPlaybackOffsetSeconds = 0;
        _pendingSeqTags = null;
        _selectedSeqWaypointIndex = -1;
        _vm?.SequenceWaypointEditor.Close();
        _renderer.SetSequencePathOverlay(false);
        if (_vm is not null)
            PostSequenceWaypointTags(null);

        if (restorePriorMount && _vm is not null && !string.IsNullOrEmpty(_seqMountedToolBefore))
            MountToolByName(_seqMountedToolBefore);

        _seqMountedToolBefore = null;
        _seqVisualMounted = null;
        if (_vm is not null)
            _vm.ActiveToolChangeSequenceId = null;
        GlCanvas.RequestNextFrameRendering();
    }

    NVec3 JointToRobrootMm(float[] joints)
    {
        if (_ikSolver is GltfNumericalIkSolver solver)
        {
            var scene = solver.ComputeFlangePosScene(joints);
            return new NVec3(
                scene.X - _robrootWorldPos.X,
                scene.Y - _robrootWorldPos.Y,
                scene.Z - _robrootWorldPos.Z);
        }
        return NVec3.Zero;
    }

    static TkVector3 ToTk(NVec3 v) => new(v.X, v.Y, v.Z);
}