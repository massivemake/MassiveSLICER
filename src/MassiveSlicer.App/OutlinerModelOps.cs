using System.IO;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.App;

/// <summary>Reload / replace eligibility for outliner mesh geometry.</summary>
internal static class OutlinerModelOps
{
    public static bool IsToolpath(SceneNode node)
        => node.Name.Contains("Toolpath", StringComparison.OrdinalIgnoreCase);

    public static bool HasMeshGeometry(SceneNode node)
    {
        if (IsToolpath(node)) return false;

        foreach (var n in node.SelfAndDescendants())
        {
            if (n.Mesh?.PickingData is not null || n.PendingMesh is not null)
                return true;
        }

        return false;
    }

    public static string? ResolveSourceFilePath(SceneNode node)
    {
        if (node.SourceFilePath is { } own && File.Exists(own))
            return own;

        foreach (var n in node.SelfAndDescendants())
        {
            if (n.SourceFilePath is { } path && File.Exists(path))
                return path;
        }

        return null;
    }

    public static bool CanReplace(OutlinerItemViewModel item)
        => item.CanDelete && HasMeshGeometry(item.Node);

    public static bool CanReload(OutlinerItemViewModel item)
        => CanReplace(item) && ResolveSourceFilePath(item.Node) is not null;
}