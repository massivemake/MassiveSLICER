using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App;

/// <summary>Builds and restores <see cref="WorkspaceDocument"/> from live application state.</summary>
internal static class WorkspaceService
{
    /// <summary>
    /// True when the .mass file at <paramref name="path"/> exists and holds at least one
    /// model entry. Used to stop an empty scene from silently overwriting a real workspace
    /// via a stale LastWorkspacePath. Unreadable/corrupt files return false (overwriting
    /// them loses nothing recoverable).
    /// </summary>
    public static bool FileHasModels(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Name.Equals("Models", StringComparison.OrdinalIgnoreCase))
                    return prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array
                           && prop.Value.GetArrayLength() > 0;
            return false;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>Captures workspace state on the UI thread (clones toolpaths; no JSON serialization yet).</summary>
    public static WorkspaceCaptureState Capture(
        ViewModels.ViewportViewModel viewport,
        ViewModels.RightPanelViewModel rightPanel,
        AppPreferences prefs,
        string savePath)
    {
        var doc = new WorkspaceDocument
        {
            CellPath       = WorkspaceCellPath.NormalizeForSave(viewport.ActiveCellPath),
            Camera         = viewport.GetCameraState?.Invoke(),
            RightPanelTab  = rightPanel.ActiveTab.ToString(),
            Settings       = ClonePreferences(prefs),
            Erp            = viewport.Erp.CurrentAttachment,
            UiSession      = CaptureUiSession(viewport),
        };

        var state = new WorkspaceCaptureState { Document = doc };

        string meshDir = WorkspaceLoader.MeshesDirFor(savePath);
        Directory.CreateDirectory(meshDir);

        string? scrubModelName = null;
        string? scrubToolpathName = null;
        var activeScrub = viewport.ActiveScrubToolpath;

        foreach (var item in viewport.EnumerateUserModelItems())
        {
            var node = item.Node;
            var entry = new WorkspaceModelEntry
            {
                Name           = node.Name,
                Visible        = node.Visible,
                LayerPreview   = node.LayerPreview,
                LocalTransform = ToArray(node.WorldTransform),
            };

            // EnumerateUserModelItems() flattens Applied-Pieces groups (see its own comment) so
            // each piece is independently slicable/exportable — but that means the group
            // membership itself would otherwise be lost on save. Recover it here so the group can
            // be recreated on load instead of every piece reappearing flat at the outliner root.
            if (viewport.FindParentOutlinerItem(item) is { IsPiecesGroup: true } piecesGroup)
                entry.PiecesGroupName = piecesGroup.Name;

            if (node.SourceFilePath is { } src && File.Exists(src))
                entry.SourcePath = src;

            // Always embed a portable copy of the mesh beside the .mass (workspace_meshes/)
            // so the workspace opens on any machine, even when SourcePath is an absolute
            // path from the machine it was saved on. Load prefers SourcePath, then a file
            // beside the .mass, then this embedded copy.
            if (TryGetMesh(node) is { } mesh)
            {
                string fileName = $"{Guid.NewGuid():N}.stl";
                string meshPath = Path.Combine(meshDir, fileName);
                StlExporter.Write(meshPath, mesh);
                entry.EmbeddedMeshPath = WorkspaceLoader.ToRelativeMeshPath(fileName);
            }
            else if (entry.SourcePath is null)
            {
                continue;
            }

            foreach (var child in item.Children)
            {
                if (viewport.GetToolpathSnapshot?.Invoke(child.Node) is not { } snap)
                    continue;

                if (activeScrub is not null
                    && (ReferenceEquals(snap.Smoothed, activeScrub)
                        || ReferenceEquals(snap.Raw, activeScrub)))
                {
                    scrubModelName = node.Name;
                    scrubToolpathName = child.Node.Name;
                }

                var tpEntry = new WorkspaceToolpathEntry
                {
                    Name           = child.Node.Name,
                    Visible        = child.Visible,
                    LocalTransform = ToArray(child.Node.LocalTransform),
                    BeadWidth      = snap.BeadWidth,
                    LayerHeight    = snap.LayerHeight,
                    MaterialColor  =
                    [
                        snap.MaterialColor.X,
                        snap.MaterialColor.Y,
                        snap.MaterialColor.Z,
                    ],
                };
                entry.Toolpaths.Add(tpEntry);
                state.ToolpathEntries.Add((tpEntry, ToolpathClone.Copy(snap.Raw)));
            }

            // Save non-destructive modifiers (Cut planes) in the model's stack
            var modifiersGroup = item.Children.FirstOrDefault(c => c.IsModifiersGroup);
            if (modifiersGroup?.Node.Children is not null)
            {
                foreach (var modifierNode in modifiersGroup.Node.Children)
                {
                    if (viewport.FindModifierForNode(modifierNode) is not { } cut)
                        continue;

                    var modEntry = new WorkspaceCutModifier
                    {
                        Name              = cut.Name,
                        Enabled           = cut.Enabled,
                        PreviewVisible    = cut.PreviewVisible,
                        Cut               = cut.Cut,
                        Orientation       = cut.Orientation.ToString(),
                        RotationDegrees   = cut.RotationDegrees,
                        Offset            = cut.Offset,
                        PositionX         = cut.PositionX,
                        PositionY         = cut.PositionY,
                        PositionZ         = cut.PositionZ,
                        PositionTangent   = cut.PositionTangent,
                        Infinite          = cut.Infinite,
                        SizeX             = cut.SizeX,
                        SizeY             = cut.SizeY,
                    };
                    entry.Modifiers.Add(modEntry);
                }
            }

            doc.Models.Add(entry);
        }

        if (doc.UiSession is not null)
        {
            doc.UiSession.ScrubModelName = scrubModelName;
            doc.UiSession.ScrubToolpathName = scrubToolpathName;
        }

        return state;
    }

    /// <summary>Snapshots edit mode, paint tools, markers UI, and layer isolation window.</summary>
    private static WorkspaceUiSession CaptureUiSession(ViewModels.ViewportViewModel viewport)
    {
        return new WorkspaceUiSession
        {
            ViewMode                 = viewport.ViewMode,
            IsPaintEditOpen          = viewport.IsPaintEditOpen,
            IsSlicePlaneViewerActive = viewport.IsSlicePlaneViewerActive,
            ShowMultiPlanarPlanes    = viewport.ShowMultiPlanarPlanes,
            XBracingShowHelper       = viewport.AdditiveSettings?.XBracingShowHelper,
            PaintHandActive          = viewport.PaintHandActive,
            PaintBoxSelectActive     = viewport.PaintBoxSelectActive,
            PaintBridgeActive        = viewport.PaintBridgeActive,
            PaintRemoveActive        = viewport.PaintRemoveActive,
            PaintLineBridgeActive    = viewport.PaintLineBridgeActive,
            PaintLineRemoveActive    = viewport.PaintLineRemoveActive,
            PaintSelectGranularity   = viewport.PaintSelectGranularity,
            PaintPickFilter          = viewport.PaintPickFilter,
            PaintBrushRadiusMm       = viewport.PaintBrushRadiusMm,
            PaintRegionSelectMode    = viewport.PaintRegionSelectMode,
            PaintModificationMode    = viewport.PaintModificationMode,
            PaintSupportType         = viewport.PaintSupportType,
            ShowPaintMarkers         = viewport.ShowPaintMarkers,
            PaintShowBeads           = viewport.PaintShowBeads,
            PaintModifications       = viewport.CapturePaintModifications?.Invoke() ?? [],
            ToolpathScrubIndex       = viewport.ToolpathScrubIndex,
            ToolpathScrubLowIndex    = viewport.ToolpathScrubLowIndex,
            ToolpathScrubLayerHigh   = viewport.ToolpathScrubLayerHigh,
            ToolpathScrubLayerLow    = viewport.ToolpathScrubLayerLow,
            IsScrubSessionActive     = viewport.IsScrubSessionActive,
            SelectToolpath           = viewport.IsToolpathSelected,
            RealtimeSlicingPaused    = viewport.RealtimeSlicingPaused,
            RobotJoints              = viewport.Robot is { } robot
                ? [robot.A1, robot.A2, robot.A3, robot.A4, robot.A5, robot.A6, robot.E1]
                : null,
            SimCameraKeyframes       = viewport.CaptureSimCameraKeyframes(),
        };
    }

    /// <summary>Serializes captured toolpaths and writes the workspace file (safe on a worker thread).</summary>
    public static void FinalizeAndSave(WorkspaceCaptureState state, string savePath)
    {
        foreach (var (entry, raw) in state.ToolpathEntries)
            entry.RawData = ToolpathSerializer.ToData(raw);

        WorkspaceLoader.Save(state.Document, savePath);
    }

    /// <summary>Restores models (and their toolpaths) into the viewport. Returns the number
    /// of model entries that produced scene content — a mesh node, or, when the mesh is
    /// missing, a placeholder hosting the entry's toolpaths.</summary>
    public static int RestoreModels(
        WorkspaceDocument doc,
        ViewModels.ViewportViewModel viewport,
        string workspacePath)
    {
        viewport.ClearUserScene();

        var piecesGroups = new Dictionary<string, ViewModels.OutlinerItemViewModel>(StringComparer.OrdinalIgnoreCase);

        int restored = 0;
        foreach (var entry in doc.Models)
        {
            // Resolve the mesh via a sequential fallback so a portable embedded copy is
            // used even when an absolute SourcePath is present but missing on this machine.
            string? loadPath = null;
            if (entry.SourcePath is { } src && File.Exists(src))
                loadPath = src;
            if (loadPath is null && entry.SourcePath is { } missingSrc)
                loadPath = TryResolveModelBesideWorkspace(workspacePath, missingSrc);
            if (loadPath is null && entry.EmbeddedMeshPath is { } rel)
            {
                string embedded = WorkspaceLoader.ResolveMeshPath(workspacePath, rel);
                if (File.Exists(embedded))
                    loadPath = embedded;
            }

            SceneNode? node = null;
            ViewModels.OutlinerItemViewModel? parentItem = null;
            if (loadPath is not null)
            {
                var transform = FromArray(entry.LocalTransform);
                node = ImportHelper.LoadAtTransform(loadPath, transform);
                if (node is not null)
                {
                    node.Name         = entry.Name;
                    node.Visible      = entry.Visible;
                    node.LayerPreview = entry.LayerPreview;

                    if (entry.PiecesGroupName is { } groupName)
                    {
                        if (!piecesGroups.TryGetValue(groupName, out var groupItem))
                        {
                            groupItem = viewport.CreateAppliedPiecesGroupNamed(groupName);
                            groupItem.Visible = entry.Visible;
                            piecesGroups[groupName] = groupItem;
                        }
                        parentItem = viewport.AddRestoredPieceToGroup(node, groupItem);
                    }
                    else
                    {
                        viewport.AddImportNode(node);
                    }
                }
            }

            // Mesh missing or failed to load. Toolpaths carry their own world coordinates,
            // so keep them under a placeholder node rather than silently dropping them
            // (the old behaviour skipped the whole entry, losing every toolpath with it).
            if (node is null)
            {
                if (entry.SourcePath is { Length: > 0 } miss)
                    viewport.OnDevLog?.Invoke($"[workspace] Mesh not found for '{entry.Name}': {miss}");

                if (entry.Toolpaths.Count == 0)
                    continue;

                node = new SceneNode
                {
                    Name           = string.IsNullOrEmpty(entry.Name)
                        ? "Toolpaths (mesh missing)"
                        : $"{entry.Name} (mesh missing)",
                    Visible        = entry.Visible,
                    LocalTransform = FromArray(entry.LocalTransform),
                };
                viewport.AddImportNode(node);
                viewport.OnDevLog?.Invoke(
                    $"[workspace] Kept {entry.Toolpaths.Count} toolpath(s) for '{entry.Name}' without its mesh.");
            }

            restored++;

            // NOTE: previously this bailed out early ("continue") whenever a model had zero
            // toolpaths — which silently skipped the Modifiers-restore loop below for any entry
            // in that state, including the master mesh after an Apply (its own pre-cut toolpath
            // is deliberately deleted, not hidden, once real per-piece toolpaths exist — see
            // ApplyModifierStackAsync). That made a saved Cut modifier stack vanish on reload
            // even though Capture() had written it correctly. Both loops below are no-ops on
            // empty lists, so there's no need to special-case either one.
            parentItem ??= viewport.FindOutlinerItem(node);
            if (parentItem is null)
            {
                viewport.NotifyRenderNeeded();
                continue;
            }

            foreach (var tpEntry in entry.Toolpaths)
            {
                var raw = tpEntry.RawData is { Layers.Count: > 0 }
                    ? ToolpathSerializer.FromData(tpEntry.RawData)
                    : ToolpathSerializer.FromData(tpEntry.Data);
                var smoothed = tpEntry.Data is { Layers.Count: > 0 } && tpEntry.RawData is { Layers.Count: > 0 }
                    ? ToolpathSerializer.FromData(tpEntry.Data)
                    : raw;

                var tpNode = new SceneNode
                {
                    Name       = tpEntry.Name,
                    Selectable = true,
                };
                tpNode.Visible = tpEntry.Visible;

                viewport.RegisterToolpathInOutliner(tpNode, parentItem);
                viewport.PendingToolpath.Enqueue(new ViewModels.PendingToolpathEntry
                {
                    Toolpath               = smoothed,
                    RawToolpath            = raw,
                    Node                   = tpNode,
                    BeadWidth              = tpEntry.BeadWidth,
                    LayerHeight            = tpEntry.LayerHeight,
                    MaterialColor          = tpEntry.MaterialColor.Length >= 3
                        ? new System.Numerics.Vector3(
                            tpEntry.MaterialColor[0],
                            tpEntry.MaterialColor[1],
                            tpEntry.MaterialColor[2])
                        : default,
                    LocalTransformOverride = FromArray(tpEntry.LocalTransform),
                });
            }

            // Restore non-destructive modifiers (Cut planes)
            foreach (var modEntry in entry.Modifiers)
            {
                var cut = viewport.AddCutModifier(parentItem, modEntry.Name);
                cut.Enabled         = modEntry.Enabled;
                cut.PreviewVisible  = modEntry.PreviewVisible;
                cut.Cut             = modEntry.Cut;
                cut.Orientation     = Enum.Parse<CutOrientation>(modEntry.Orientation);
                cut.RotationDegrees = modEntry.RotationDegrees;
                cut.Offset          = modEntry.Offset;
                cut.PositionX       = modEntry.PositionX;
                cut.PositionY       = modEntry.PositionY;
                cut.PositionZ       = modEntry.PositionZ;
                cut.PositionTangent = modEntry.PositionTangent;
                cut.Infinite        = modEntry.Infinite;
                cut.SizeX           = modEntry.SizeX;
                cut.SizeY           = modEntry.SizeY;

                // AddCutModifier() already built this modifier's gizmo node — with the
                // Orientation/Infinite/Size defaults it had at that moment, since none of the
                // saved fields above were assigned yet. Every live panel edit re-syncs the
                // gizmo's transform and rebuilds its plane mesh after a field change; restore
                // must do the same or the gizmo (what ApplyModifierStackAsync actually reads
                // the cut geometry from) stays stuck at its creation-time defaults even though
                // the CutModifier's own fields are correct.
                viewport.SyncModifierGizmoNodeFromFields(cut);
                viewport.RebuildModifierPlaneMesh(cut);
            }

            viewport.NotifyRenderNeeded();
        }

        return restored;
    }

    private static MeshData? TryGetMesh(SceneNode root)
    {
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is { } pending) return pending;
            if (n.Mesh?.PickingData is { } gpu) return gpu;
        }
        return null;
    }

    /// <summary>
    /// When the saved source path (e.g. <c>Z:\...</c>) is missing, try the same filename beside the
    /// <c>.mass</c> file — common when projects live on NAS but drive letters differ per PC.
    /// </summary>
    private static string? TryResolveModelBesideWorkspace(string workspacePath, string originalSource)
    {
        workspacePath = PathNormalization.Normalize(workspacePath);
        string fileName = Path.GetFileName(originalSource);
        if (fileName.Length == 0) return null;

        string? dir = Path.GetDirectoryName(workspacePath);
        if (dir is null) return null;

        string sibling = Path.Combine(dir, fileName);
        return File.Exists(sibling) ? sibling : null;
    }

    private static AppPreferences ClonePreferences(AppPreferences src)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(src);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppPreferences>(json) ?? new AppPreferences();
        // .mass files travel across machines/NAS — the ERP bearer token and robot
        // SMB passwords stay in the local prefs.json only, never in workspace
        // settings snapshots.
        clone.ErpApiToken = null;
        foreach (var smb in clone.RobotSmb)
            smb.Password = null;
        return clone;
    }

    private static float[] ToArray(Matrix4 m) =>
    [
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    ];

    private static Matrix4 FromArray(float[] a)
    {
        if (a.Length < 16) return Matrix4.Identity;
        return new Matrix4(
            a[0],  a[1],  a[2],  a[3],
            a[4],  a[5],  a[6],  a[7],
            a[8],  a[9],  a[10], a[11],
            a[12], a[13], a[14], a[15]);
    }
}