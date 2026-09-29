using System.Numerics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using NVec3 = System.Numerics.Vector3;

namespace MassiveSlicer.App.Views;

public partial class ViewportView
{
    /// <summary>Set while this view is alive so the console can export without a window cast.</summary>
    internal static ViewportView? PolylineExportView { get; private set; }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        PolylineExportView = this;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (ReferenceEquals(PolylineExportView, this))
            PolylineExportView = null;
        base.OnDetachedFromVisualTree(e);
    }

    public Task ExportPolylineAsync(SceneNode? node = null)
        => ExportPolylineCoreAsync(node, path: null);

    public Task<string?> ExportPolylineToPathAsync(string path, SceneNode? node = null)
        => ExportPolylineCoreAsync(node, path);

    async Task<string?> ExportPolylineCoreAsync(SceneNode? node, string? path)
    {
        var vm = _vm;
        if (vm is null)
        {
            return null;
        }

        if (!TryResolvePolyline(node, out var toolpath, out var drawNode, out var error))
        {
            SetSliceStatus(vm, error, isError: true);
            return null;
        }

        if (path is null)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is not { } storage)
            {
                SetSliceStatus(vm, "Export Polyline: no file dialog on this window.", isError: true);
                return null;
            }

            string suggested = PolylineObjExporter.SanitizeName(drawNode.Name);
            if (!suggested.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                suggested += ".obj";

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Polyline (Blender)",
                DefaultExtension = "obj",
                SuggestedFileName = suggested,
                FileTypeChoices =
                [
                    new FilePickerFileType("Wavefront OBJ (Blender)") { Patterns = ["*.obj"] },
                ],
            });
            path = file?.TryGetLocalPath();
            if (path is null) return null;
            if (!path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                path += ".obj";
        }

        var frame = FrameFor(drawNode);
        var chains = PolylineObjExporter.Chains(toolpath, frame);
        if (chains.Count == 0)
        {
            SetSliceStatus(vm, "Export Polyline: that path has no moves.", isError: true);
            return null;
        }

        int verts = chains.Sum(c => c.Count);
        var sb = new StringBuilder(verts * 48);
        PolylineObjExporter.Write(sb, drawNode.Name, chains);
        await File.WriteAllTextAsync(path, sb.ToString());

        SetSliceStatus(vm,
            $"Exported polyline {Path.GetFileName(path)} ({verts:N0} pts). Blender: Import Wavefront. Scale 0.001 if the scene is meters.");
        return path;
    }

    bool TryResolvePolyline(SceneNode? requested, out Toolpath toolpath, out SceneNode node, out string error)
    {
        toolpath = null!;
        node = null!;
        error = "";

        if (requested is not null && _toolpathByNode.TryGetValue(requested, out var asked) && asked is not null)
        {
            toolpath = asked;
            node = requested;
            return true;
        }

        if (_renderer.SelectedNode is { } sel && _toolpathByNode.TryGetValue(sel, out var selected) && selected is not null)
        {
            toolpath = selected;
            node = sel;
            return true;
        }

        if (_activeScrubNode is { } scrub && _toolpathByNode.TryGetValue(scrub, out var active) && active is not null)
        {
            toolpath = active;
            node = scrub;
            return true;
        }

        if (_toolpathByNode.Count == 1)
        {
            var only = _toolpathByNode.First();
            toolpath = only.Value;
            node = only.Key;
            return true;
        }

        error = _toolpathByNode.Count == 0
            ? "Export Polyline: import or slice a path first."
            : "Export Polyline: select the toolpath in the outliner first.";
        return false;
    }

    PolylineDrawFrame FrameFor(SceneNode node)
    {
        if (!_toolpathOriginByNode.TryGetValue(node, out var origin))
            origin = CentroidFallback(_toolpathByNode[node]);

        var m = node.LocalTransform;
        return new PolylineDrawFrame(
            new System.Numerics.Vector3(origin.X, origin.Y, origin.Z),
            m.M11, m.M21, m.M31, m.M41,
            m.M12, m.M22, m.M32, m.M42,
            m.M13, m.M23, m.M33, m.M43);
    }

    static System.Numerics.Vector3 CentroidFallback(Toolpath toolpath)
    {
        var sum = System.Numerics.Vector3.Zero;
        int count = 0;
        foreach (var layer in toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (!ToolpathMoveKinds.IsCutSegment(move.Kind)) continue;
                sum += move.From + move.To;
                count += 2;
            }
        }

        if (count == 0)
        {
            foreach (var layer in toolpath.Layers)
            {
                foreach (var move in layer.Moves)
                {
                    sum += move.From + move.To;
                    count += 2;
                }
            }
        }

        return count > 0 ? sum / count : System.Numerics.Vector3.Zero;
    }
}
