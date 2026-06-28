using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App.Undo;

/// <summary>Reverses a pivot recenter that shifts mesh data and subtree transforms.</summary>
public sealed class NodeRecenterAction : IUndoAction
{
    private readonly SceneNode _root;
    private readonly Dictionary<SceneNode, Matrix4> _transformsBefore;
    private readonly Dictionary<SceneNode, Matrix4> _transformsAfter;
    private readonly Dictionary<SceneNode, MeshData?> _meshesBefore;
    private readonly Dictionary<SceneNode, MeshData?> _meshesAfter;
    private readonly Action? _onApplied;

    public string Description { get; }

    public NodeRecenterAction(
        SceneNode root,
        Dictionary<SceneNode, Matrix4> transformsBefore,
        Dictionary<SceneNode, Matrix4> transformsAfter,
        Dictionary<SceneNode, MeshData?> meshesBefore,
        Dictionary<SceneNode, MeshData?> meshesAfter,
        Action? onApplied = null)
    {
        _root             = root;
        _transformsBefore = transformsBefore;
        _transformsAfter  = transformsAfter;
        _meshesBefore     = meshesBefore;
        _meshesAfter      = meshesAfter;
        _onApplied        = onApplied;
        Description       = "Recenter";
    }

    public void Undo() => Apply(_transformsBefore, _meshesBefore);
    public void Redo() => Apply(_transformsAfter, _meshesAfter);

    private void Apply(Dictionary<SceneNode, Matrix4> transforms, Dictionary<SceneNode, MeshData?> meshes)
    {
        ImportHelper.RestoreSubtreeSnapshot(_root, transforms, meshes);
        _onApplied?.Invoke();
    }
}