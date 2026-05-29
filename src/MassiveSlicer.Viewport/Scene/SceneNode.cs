using MassiveSlicer.Viewport.Rendering;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// A node in the scene graph. Holds a local transform, an optional renderable mesh,
/// and zero or more child nodes. World transform is computed on demand by walking
/// the parent chain.
/// </summary>
public sealed class SceneNode
{
    // -- Identity --------------------------------------------------------------

    /// <summary>Display name used in the outliner.</summary>
    public string Name { get; set; } = "Node";

    /// <summary>
    /// When <c>false</c> this node cannot be picked or selected by the user.
    /// Set on application-owned scene objects (robot, tool, bed) that are not
    /// user geometry.
    /// </summary>
    public bool Selectable { get; set; } = true;

    /// <summary>
    /// When <c>false</c> back-face culling is disabled while drawing this subtree.
    /// Set to <c>false</c> for user-imported models so inside faces are visible.
    /// </summary>
    public bool CullFaces { get; set; } = true;

    /// <summary>
    /// When <c>true</c> this node is drawn in a separate overlay pass after the main
    /// scene (depth buffer cleared), so it is always visible on top of other geometry.
    /// Use for markers and handles that must never be occluded.
    /// </summary>
    public bool Overlay { get; set; } = false;

    /// <summary>
    /// When <c>false</c> this node and its entire subtree are skipped during rendering.
    /// Toggle to show or hide geometry without removing it from the scene graph.
    /// </summary>
    public bool Visible { get; set; } = true;

    // -- Hierarchy -------------------------------------------------------------

    /// <summary>Parent node, or <c>null</c> for a root node.</summary>
    public SceneNode? Parent { get; private set; }

    /// <summary>Ordered list of child nodes.</summary>
    public List<SceneNode> Children { get; } = [];

    // -- Transform -------------------------------------------------------------

    /// <summary>Transform relative to the parent (or world if no parent).</summary>
    public Matrix4 LocalTransform { get; set; } = Matrix4.Identity;

    /// <summary>Accumulated world-space transform, computed from the parent chain.</summary>
    public Matrix4 WorldTransform
        => Parent is null ? LocalTransform : LocalTransform * Parent.WorldTransform;

    // -- Renderable ------------------------------------------------------------

    /// <summary>
    /// CPU-side mesh set by loaders. Consumed on the GL thread by the render loop,
    /// which creates a <see cref="MeshRenderer"/> from it and sets <see cref="Mesh"/>.
    /// </summary>
    public MeshData? PendingMesh { get; set; }

    /// <summary>Mesh attached to this node, or <c>null</c> if it is a transform-only node.</summary>
    public MeshRenderer? Mesh { get; set; }

    // -- Graph operations ------------------------------------------------------

    /// <summary>Adds <paramref name="child"/> as a child of this node and sets its parent.</summary>
    public void AddChild(SceneNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    /// <summary>Removes <paramref name="child"/> and clears its parent reference.</summary>
    public void RemoveChild(SceneNode child)
    {
        if (Children.Remove(child))
            child.Parent = null;
    }

    // -- Rendering -------------------------------------------------------------

    /// <summary>
    /// Draws this node's mesh (if any) and recurses into all children.
    /// </summary>
    /// <param name="viewProj">Combined view × projection matrix.</param>
    /// <param name="viewPos">Camera position in world space.</param>
    /// <param name="lightDir">Direction toward the light source in world space (normalised).</param>
    /// <param name="lightIntensity">Directional light multiplier (1 = default).</param>
    public void Draw(Matrix4 viewProj, Vector3 viewPos, Vector3 lightDir, float lightIntensity)
    {
        if (!Visible) return;
        var world   = WorldTransform;
        var fullMvp = world * viewProj;
        Mesh?.Draw(world, fullMvp, viewPos, lightDir, lightIntensity);

        foreach (var child in Children)
            child.Draw(viewProj, viewPos, lightDir, lightIntensity);
    }

    // -- Traversal -------------------------------------------------------------

    /// <summary>Returns this node and all its descendants in depth-first order.</summary>
    public IEnumerable<SceneNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var desc in child.SelfAndDescendants())
                yield return desc;
    }

    /// <summary>
    /// Returns the first descendant (or self) whose <see cref="Name"/> matches,
    /// using an ordinal case-sensitive comparison, or <c>null</c> if not found.
    /// </summary>
    public SceneNode? FindDescendant(string name)
    {
        foreach (var node in SelfAndDescendants())
            if (node.Name == name) return node;
        return null;
    }
}
