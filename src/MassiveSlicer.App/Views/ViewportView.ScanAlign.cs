using Avalonia.Controls;
using Avalonia.Threading;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App.Views;

public partial class ViewportView
{
    private int _alignGeneration;
    private SceneNode? _alignBaseNode;
    private Matrix4 _alignBaseLocal;
    private bool _alignBaseReady;

    /// <summary>
    /// Moves the model so it sits inside the scan by the stock thickness.
    /// The scan transform is not written. A weak fit is reported and not applied.
    /// Does not reslice and does not drop the model onto the bed.
    /// </summary>
    private async Task AlignModelToScanAsync()
    {
        if (DataContext is not ViewportViewModel vm) return;
        var scanVm = ScanSettings();
        if (scanVm is null || scanVm.IsAligning) return;

        if (!TryResolveAlignPair(vm, out var model, out var scan, out var error))
        {
            scanVm.SetAlignStatus(error, isError: true);
            LogAlign(error);
            return;
        }

        var modelPoints = new List<Vector3>();
        var modelNormals = new List<Vector3>();
        var scanPoints = new List<Vector3>();
        CollectAlignSamples(model.Node, modelPoints, modelNormals, ScanModelAligner.MaxSamplePoints);
        CollectAlignSamples(scan.Node, scanPoints, normals: null, ScanModelAligner.MaxScanSamplePoints);

        var request = new ScanModelAligner.ScanModelAlignRequest(
            (float)scanVm.StockMm,
            (float)scanVm.ToleranceMm,
            (float)(scanVm.MatchPercent / 100.0),
            (float)scanVm.MaxShiftMm,
            (float)scanVm.MaxTurnDeg);

        scanVm.IsAligning = true;
        scanVm.SetAlignStatus("Matching…", isError: false);
        int generation = ++_alignGeneration;
        var scanBefore = scan.Node.LocalTransform;
        try
        {
            var result = await Task.Run(() =>
                ScanModelAligner.Align(modelPoints, modelNormals, scanPoints, request));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _alignGeneration) return;
                if (scan.Node.LocalTransform != scanBefore)
                {
                    const string moved = "Not applied. The scan changed during the match.";
                    scanVm.SetAlignStatus(moved, isError: true);
                    LogAlign(moved);
                    return;
                }

                if (!result.Accepted)
                {
                    scanVm.SetAlignStatus(result.Message, isError: true);
                    LogAlign($"[scan-align] {result.Message}");
                    return;
                }

                var before = model.Node.LocalTransform;
                ApplyWorldTransformToNode(model.Node, result.ModelWorldDelta);
                MirrorTypedTransformDelta(vm, model.Node, before);
                _alignBaseNode = model.Node;
                _alignBaseLocal = model.Node.LocalTransform;
                _alignBaseReady = true;
                scanVm.ResetAlignOffsets();
                string message =
                    $"Matched {model.Name} to {scan.Name}. {result.Message} The scan was not moved.";
                scanVm.SetAlignStatus(message, isError: false);
                LogAlign($"[scan-align] {message}");
                SyncSelectionTransformDisplay(vm, remember: false);
                GlCanvas.RequestNextFrameRendering();
                SchedulePanelTransformUndo(vm, model.Node, "Align", before);
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation == _alignGeneration)
                    scanVm.IsAligning = false;
            });
        }
    }

    /// <summary>Puts the model back on the accepted pose, then applies the nudge fields.</summary>
    private void ApplyAlignNudge()
    {
        if (DataContext is not ViewportViewModel vm) return;
        var scanVm = ScanSettings();
        if (scanVm is null || scanVm.IsAligning) return;
        if (!TryResolveAlignPair(vm, out var model, out _, out var error))
        {
            scanVm.SetAlignStatus(error, isError: true);
            return;
        }

        if (!_alignBaseReady || !ReferenceEquals(_alignBaseNode, model.Node))
        {
            _alignBaseNode = model.Node;
            _alignBaseLocal = model.Node.LocalTransform;
            _alignBaseReady = true;
        }

        var before = model.Node.LocalTransform;
        model.Node.LocalTransform = _alignBaseLocal;
        var pivot = model.Node.Placement is { } placement
            ? Vector3.TransformPosition(placement.Origin, model.Node.WorldTransform)
            : model.Node.WorldTransform.Row3.Xyz;
        var nudge = ScanModelAligner.WorldNudge(
            pivot,
            new Vector3((float)scanVm.OffsetX, (float)scanVm.OffsetY, (float)scanVm.OffsetZ),
            new Vector3((float)scanVm.RotateX, (float)scanVm.RotateY, (float)scanVm.RotateZ));
        ApplyWorldTransformToNode(model.Node, nudge);
        MirrorTypedTransformDelta(vm, model.Node, before);
        SyncSelectionTransformDisplay(vm, remember: false);
        GlCanvas.RequestNextFrameRendering();
        SchedulePanelTransformUndo(vm, model.Node, "Align nudge", before);
    }

    private ScanSettingsViewModel? ScanSettings()
        => TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main
            ? main.RightPanel.Scan
            : null;

    private void LogAlign(string message)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main)
            main.Console.Log(message);
    }

    private static bool TryResolveAlignPair(
        ViewportViewModel vm,
        out OutlinerItemViewModel model,
        out OutlinerItemViewModel scan,
        out string error)
    {
        model = null!;
        scan = null!;
        error = "";

        var selectedScans = vm.SelectedScanItems.Where(OutlinerModelOps.IsScanItem).ToList();
        if (selectedScans.Count > 1)
        {
            error = "Not applied. Select one scan. Use the merged scan when the print was merged.";
            return false;
        }

        if (selectedScans.Count == 1)
            scan = selectedScans[0];
        else
        {
            var visible = vm.GetBedLevelScanItems().Where(i => i.Visible && OutlinerModelOps.IsScanItem(i)).ToList();
            var merged = visible.Where(i => i.Name.StartsWith("Merged Scan", StringComparison.OrdinalIgnoreCase)).ToList();
            if (merged.Count == 1)
                scan = merged[0];
            else if (visible.Count == 1)
                scan = visible[0];
            else
            {
                error = "Not applied. Select the scan that marks where to mill.";
                return false;
            }
        }

        var owner = vm.OwningModelItem(vm.SelectedOutlinerItem);
        if (owner is not null
            && !OutlinerModelOps.IsScanItem(owner)
            && !owner.IsToolpath
            && OutlinerModelOps.HasMeshGeometry(owner.Node))
        {
            model = owner;
        }
        else
        {
            var models = vm.GetUserModelItems()
                .Where(i => i.Visible
                            && !i.IsToolpath
                            && !OutlinerModelOps.IsScanItem(i)
                            && OutlinerModelOps.HasMeshGeometry(i.Node))
                .ToList();
            if (models.Count != 1)
            {
                error = "Not applied. Select the model to move. The scan stays where it is.";
                return false;
            }
            model = models[0];
        }

        if (ReferenceEquals(model.Node, scan.Node))
        {
            error = "Not applied. Select the model to move. The scan stays where it is.";
            return false;
        }

        return true;
    }

    private static void CollectAlignSamples(
        SceneNode root, List<Vector3> points, List<Vector3>? normals, int maxPoints)
    {
        var meshes = new List<MeshData>();
        var worlds = new List<Matrix4>();
        foreach (var node in root.SelfAndDescendants())
        {
            if (OutlinerModelOps.IsToolpath(node) || OutlinerModelOps.IsScan(node) && !ReferenceEquals(node, root))
                continue;
            var mesh = node.Mesh?.PickingData ?? node.PendingMesh;
            if (mesh is null || mesh.Positions.Length < 3) continue;
            meshes.Add(mesh);
            worlds.Add(node.WorldTransform);
        }

        ScanModelAligner.CollectBalancedSamples(meshes, worlds, maxPoints, points, normals);
    }
}
