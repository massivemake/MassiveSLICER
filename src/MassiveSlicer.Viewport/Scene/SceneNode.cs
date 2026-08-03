using MassiveSlicer.Viewport.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>Ray-pick priority — content wins over cell environment (bed, rotary table, stands).</summary>
public enum PickTier
{
    /// <summary>User geometry, scans, toolpaths.</summary>
    Content = 0,
    /// <summary>Cell fixtures: print bed, rotary bed, stands, docks (dev-mode only).</summary>
    Environment = 1,
}

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
    /// Original file path when this node was imported from disk.
    /// Used by workspace save/restore; may be shared across exploded shells.
    /// </summary>
    public string? SourceFilePath { get; set; }

    /// <summary>
    /// When <c>false</c> this node cannot be picked or selected by the user.
    /// Set on application-owned scene objects (robot, tool, bed) that are not
    /// user geometry.
    /// </summary>
    public bool Selectable { get; set; } = true;

    /// <summary>
    /// Pick order when multiple surfaces align — lower tier wins; within a tier, closer hit wins.
    /// </summary>
    public PickTier PickTier { get; set; } = PickTier.Content;

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

    /// <summary>When true, viewport shader modes (MatteBlack, Clay, …) leave this
    /// node's own mesh material untouched (used by gizmo-like handles, e.g. effectors).</summary>
    public bool KeepOwnMaterial { get; set; } = false;

    /// <summary>Drawn in the late translucent pass (depth-tested, no depth write) so
    /// geometry and toolpaths inside the volume stay visible (e.g. effector range glow).</summary>
    public bool TranslucentPass { get; set; } = false;

    /// <summary>Excluded from ray picking entirely (never steals clicks).</summary>
    public bool PickIgnore { get; set; } = false;

    /// <summary>
    /// Authoring-only overlay geometry (e.g. a modifier's gizmo plane) — real, pickable, and
    /// rendered like any other node, but never real model content. Excluded from mesh-collection
    /// utilities (slicing, bounding-box, export) so it never leaks into a toolpath or measurement,
    /// even though it lives as a genuine child of the mesh it belongs to.
    /// </summary>
    public bool IsAuthoringOverlay { get; set; } = false;

    /// <summary>
    /// Drawn (and mask-tested for the selection outline) with depth testing disabled, so it
    /// always renders fully on top rather than being partially occluded by real scene geometry
    /// it happens to overlap -- for small precise UI indicators (e.g. a modifier's Infinite/
    /// Restricted corner markers) where a partial occlusion reads as a rendering glitch (half
    /// shaded/outlined differently from the other half) rather than legible depth cueing, unlike
    /// a large flat surface (e.g. the modifier's own plane fill) where partial occlusion by real
    /// geometry is expected and fine. Deliberately separate from IsAuthoringOverlay, which the
    /// plane fill also sets for an unrelated reason (excluding it from mesh-collection utilities).
    /// </summary>
    public bool AlwaysOnTop { get; set; } = false;

    /// <summary>
    /// When <c>false</c> this node and its entire subtree are skipped during rendering.
    /// Toggle to show or hide geometry without removing it from the scene graph.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// When <c>true</c> the layer-preview stripe shader is forced on for this node and its
    /// descendants, regardless of the global <see cref="SceneRenderer.ShaderMode"/>.
    /// Set by the Additive panel toggle; only applies to source mesh nodes.
    /// </summary>
    public bool LayerPreview { get; set; }

    // -- Hierarchy -------------------------------------------------------------

    /// <summary>Parent node, or <c>null</c> for a root node.</summary>
    public SceneNode? Parent { get; private set; }

    /// <summary>Ordered list of child nodes.</summary>
    public List<SceneNode> Children { get; } = [];

    // -- Transform -------------------------------------------------------------

    private Matrix4        _localTransform = Matrix4.Identity;
    private NodeTransform? _placement;

    /// <summary>Transform relative to the parent (or world if no parent).</summary>
    /// <remarks>
    /// Still the single thing every reader consumes. When <see cref="Placement"/> is set, writing a
    /// raw matrix here re-derives the separated values from it rather than leaving them stale — so
    /// the two can never disagree, and code paths that legitimately hand over a finished matrix
    /// (undo/redo restore, workspace load, the robot and cell rigs) keep working untouched.
    /// </remarks>
    public Matrix4 LocalTransform
    {
        get => _localTransform;
        set
        {
            _localTransform = value;
            // Keep the pivot; re-read position/rotation/scale from whatever was just written.
            if (_placement is { } p)
                _placement = NodeTransform.FromMatrix(value, p.Origin);
        }
    }

    /// <summary>
    /// The node's placement as separated position / rotation / scale / pivot values, or
    /// <c>null</c> for nodes that are driven straight from a matrix (the robot rig, cell fixtures,
    /// the tool) and have no user-facing transform tools.
    /// </summary>
    /// <remarks>
    /// Set this via <see cref="SetPlacement"/> so the composed matrix is rebuilt with it. Reading
    /// is free; the getter does no work.
    /// </remarks>
    public NodeTransform? Placement => _placement;

    /// <summary>Replaces <see cref="Placement"/> and rebuilds <see cref="LocalTransform"/> from it.</summary>
    public void SetPlacement(NodeTransform placement)
    {
        _placement      = placement;
        _localTransform = placement.ToMatrix();
    }

    /// <summary>
    /// Gives this node a <see cref="Placement"/> if it has none, decomposed from its current
    /// matrix and pivoting about <paramref name="origin"/> in mesh space. The composed transform is
    /// unchanged either way, so adopting a placement never moves anything.
    /// </summary>
    public NodeTransform EnsurePlacement(Vector3 origin)
    {
        if (_placement is null)
        {
            _placement  = NodeTransform.FromMatrix(_localTransform, origin);
            ImportScale = _placement.Value.Scale;
        }
        return _placement.Value;
    }

    /// <summary>
    /// The scale this node had when it first adopted a placement — its "as imported" size, and the
    /// 100% the scale tool's percent mode is a percentage of.
    /// </summary>
    /// <remarks>
    /// Not simply 1: an STL exported in metres is corrected ×1000 before the placement is taken, and
    /// a part imported into a rotary cell is scaled down to fit the platter. Both are baked into the
    /// matrix that <see cref="EnsurePlacement"/> decomposes, so the raw <see cref="NodeTransform.Scale"/>
    /// is a poor answer to "how big is this compared to the file I opened". Captured at the same
    /// moment as the pivot, once, and never recomputed.
    /// <para>
    /// <c>null</c> on a node restored from a workspace saved before this existed; callers should
    /// read that as <see cref="Vector3.One"/>, which is what the raw scale already meant.
    /// </para>
    /// </remarks>
    public Vector3? ImportScale { get; set; }

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
        if (Mesh is { } mesh)
        {
            // Set explicitly (not toggle-and-restore) so this is correct regardless of whatever
            // state a parent or earlier sibling left behind — the caller previously only ever
            // checked CullFaces on SceneRoot's direct children before drawing each entire
            // subtree in one shot, silently ignoring every individual mesh node's own flag
            // (e.g. imported meshes are set CullFaces=false at import specifically so their
            // inside faces stay visible, several levels below any top-level child — that
            // setting was dead code).
            if (CullFaces) GL.Enable(EnableCap.CullFace); else GL.Disable(EnableCap.CullFace);
            // AlwaysOnTop used to be honoured only by the translucent pass and the selection-mask
            // pass, so an OPAQUE node carrying the flag was silently depth-tested anyway. The Move
            // Origin snap markers are exactly that, which is why the ones on the far side of a part
            // never showed through it and the gold centre marker — inside the mesh by definition —
            // could not be seen at all. Same explicit set/restore shape as CullFaces above.
            //
            // ⚠ The restore re-enables unconditionally, which is right for every caller today: the
            // opaque pass always enters with depth testing on. The one caller that does not is the
            // translucent pass, which disables it around this call for an AlwaysOnTop node and
            // re-enables afterwards — harmless because it only wraps the node's OWN mesh here, and
            // the sole such node (a cut modifier's corner markers) is a leaf. Give a translucent
            // always-on-top node children and their depth state would need rethinking.
            if (AlwaysOnTop) GL.Disable(EnableCap.DepthTest);
            mesh.Draw(world, fullMvp, viewPos, lightDir, lightIntensity);
            if (AlwaysOnTop) GL.Enable(EnableCap.DepthTest);
        }

        foreach (var child in Children)
            if (!child.TranslucentPass)
                child.Draw(viewProj, viewPos, lightDir, lightIntensity);
    }

    // -- Traversal -------------------------------------------------------------

    /// <summary>Marks this node and its descendants as non-selectable cell environment geometry.</summary>
    public void MarkEnvironmentSubtree()
    {
        foreach (var n in SelfAndDescendants())
        {
            n.PickTier    = PickTier.Environment;
            n.Selectable  = false;
        }
    }

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
