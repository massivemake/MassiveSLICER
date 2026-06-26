namespace MassiveSlicer.Viewport.Scene;

/// <summary>Deep-clones scene node trees while sharing <see cref="MeshData"/> references.</summary>
public static class SceneNodeClone
{
    public static SceneNode DeepClone(SceneNode src)
    {
        var clone = new SceneNode
        {
            Name           = src.Name,
            SourceFilePath = src.SourceFilePath,
            Selectable     = src.Selectable,
            PickTier       = src.PickTier,
            CullFaces      = src.CullFaces,
            Overlay        = src.Overlay,
            Visible        = src.Visible,
            LayerPreview   = src.LayerPreview,
            LocalTransform = src.LocalTransform,
            PendingMesh    = src.PendingMesh,
        };

        foreach (var child in src.Children)
            clone.AddChild(DeepClone(child));

        return clone;
    }
}