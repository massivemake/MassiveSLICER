using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App;

/// <summary>Builds and restores <see cref="WorkspaceDocument"/> from live application state.</summary>
internal static class WorkspaceService
{
    public static WorkspaceDocument Build(
        ViewModels.ViewportViewModel viewport,
        ViewModels.RightPanelViewModel rightPanel,
        AppPreferences prefs,
        string savePath)
    {
        var doc = new WorkspaceDocument
        {
            CellPath       = viewport.ActiveCellPath,
            Camera         = viewport.GetCameraState?.Invoke(),
            RightPanelTab  = rightPanel.ActiveTab.ToString(),
            Settings       = ClonePreferences(prefs),
        };

        string meshDir = WorkspaceLoader.MeshesDirFor(savePath);
        Directory.CreateDirectory(meshDir);

        foreach (var item in viewport.OutlinerItems)
        {
            var node = item.Node;
            var entry = new WorkspaceModelEntry
            {
                Name          = node.Name,
                Visible       = node.Visible,
                LayerPreview  = node.LayerPreview,
                LocalTransform = ToArray(node.LocalTransform),
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

                entry.Toolpaths.Add(new WorkspaceToolpathEntry
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
                    Data    = ToolpathSerializer.ToData(snap.Smoothed),
                    RawData = ReferenceEquals(snap.Smoothed, snap.Raw)
                        ? null
                        : ToolpathSerializer.ToData(snap.Raw),
                });
            }

            doc.Models.Add(entry);
        }

        return doc;
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
            viewport.AddUserNode(node);

            if (entry.Toolpaths.Count == 0) continue;

            var parentItem = viewport.FindOutlinerItem(node);
            if (parentItem is null) continue;

            foreach (var tpEntry in entry.Toolpaths)
            {
                var smoothed = ToolpathSerializer.FromData(tpEntry.Data);
                var raw      = tpEntry.RawData is not null
                    ? ToolpathSerializer.FromData(tpEntry.RawData)
                    : smoothed;

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