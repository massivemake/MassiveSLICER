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
        };

        var state = new WorkspaceCaptureState { Document = doc };

        string meshDir = WorkspaceLoader.MeshesDirFor(savePath);
        Directory.CreateDirectory(meshDir);

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

            if (node.SourceFilePath is { } src && File.Exists(src))
            {
                entry.SourcePath = src;
            }
            else if (TryGetMesh(node) is { } mesh)
            {
                string fileName = $"{Guid.NewGuid():N}.stl";
                string meshPath = Path.Combine(meshDir, fileName);
                StlExporter.Write(meshPath, mesh);
                entry.EmbeddedMeshPath = WorkspaceLoader.ToRelativeMeshPath(fileName);
            }
            else
            {
                continue;
            }

            foreach (var child in item.Children)
            {
                if (viewport.GetToolpathSnapshot?.Invoke(child.Node) is not { } snap)
                    continue;

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

            doc.Models.Add(entry);
        }

        return state;
    }

    /// <summary>Serializes captured toolpaths and writes the workspace file (safe on a worker thread).</summary>
    public static void FinalizeAndSave(WorkspaceCaptureState state, string savePath)
    {
        foreach (var (entry, raw) in state.ToolpathEntries)
            entry.RawData = ToolpathSerializer.ToData(raw);

        WorkspaceLoader.Save(state.Document, savePath);
    }

    public static void RestoreModels(
        WorkspaceDocument doc,
        ViewModels.ViewportViewModel viewport,
        string workspacePath)
    {
        viewport.ClearUserScene();

        foreach (var entry in doc.Models)
        {
            string? loadPath = null;
            if (entry.SourcePath is { } src && File.Exists(src))
                loadPath = src;
            else if (entry.SourcePath is { } missingSrc)
                loadPath = TryResolveModelBesideWorkspace(workspacePath, missingSrc);
            else if (entry.EmbeddedMeshPath is { } rel)
            {
                string embedded = WorkspaceLoader.ResolveMeshPath(workspacePath, rel);
                if (File.Exists(embedded))
                    loadPath = embedded;
            }

            if (loadPath is null) continue;

            var transform = FromArray(entry.LocalTransform);
            var node = ImportHelper.LoadAtTransform(loadPath, transform);
            if (node is null) continue;

            node.Name         = entry.Name;
            node.Visible      = entry.Visible;
            node.LayerPreview = entry.LayerPreview;
            viewport.AddImportNode(node);

            if (entry.Toolpaths.Count == 0) continue;

            var parentItem = viewport.FindOutlinerItem(node);
            if (parentItem is null) continue;

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

            viewport.NotifyRenderNeeded();
        }
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
        return System.Text.Json.JsonSerializer.Deserialize<AppPreferences>(json) ?? new AppPreferences();
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