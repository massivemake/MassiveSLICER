using System.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App;

/// <summary>
/// Shared logic for loading and placing user-imported 3D models.
/// Handles file loading, bounding-box computation, and bed-surface placement.
/// </summary>
internal static class ImportHelper
{
    private static readonly HashSet<string> SupportedExtensions = [".glb", ".gltf", ".stl", ".obj", ".3mf", ".stp", ".step"];

    /// <summary>Fraction of rotary radius used when scaling oversized imports to fit the table.</summary>
    private const float RotaryFitMargin = 0.96f;

    /// <summary>Largest bounding-box dimension (mm) below which a fresh import is assumed to be in
    /// metres (exported as millimetres) and auto-scaled ×1000. Real LFAM parts are always larger.</summary>
    private const float TinyImportMaxDimMm = 10f;

    internal static bool IsSupported(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Loads a model file and places it on the bed surface with its bounding-box
    /// centre aligned to the bed's XY centre. Returns <c>null</c> on load failure.
    /// Corrects a metres-as-millimetres import (see <see cref="NormalizeUnitScale"/>);
    /// <paramref name="log"/> receives any scale-correction warning.
    /// </summary>
    internal static SceneNode? LoadAndPlace(string filePath, CellConfig? activeCell, Action<string>? log = null)
    {
        var node = LoadFile(filePath);
        if (node is null) return null;

        NormalizeUnitScale(node, log);
        PlaceOnBed(node, activeCell);
        CenterOrigin(node);
        return node;
    }

    /// <summary>
    /// Gives <paramref name="node"/> a pivot at the centre of its own bounding box, once, without
    /// the geometry moving. Applied to every fresh import and to every piece a cut creates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the pivot is wherever the exporting package left its origin — frequently far
    /// outside the part — so the gizmo appeared detached from the mesh and every rotation swung the
    /// part around a distant point. Toolpath nodes have always centred their own origin on their
    /// centroid (<c>SceneRenderer.AddToolpath</c>); this brings meshes in line with that.
    /// </para>
    /// <para>
    /// Deliberately a one-time move at creation rather than a value recomputed as the part changes:
    /// a pivot that silently followed the geometry would shift under the user mid-edit. Re-centring
    /// later is an explicit Recenter Origin press.
    /// </para>
    /// <para>
    /// Called after <see cref="PlaceOnBed"/> so the pivot's recorded position already accounts for
    /// bed placement and any unit-scale correction. Does nothing to a node that already carries a
    /// placement, so restoring a saved workspace keeps whatever pivot that file specified.
    /// </para>
    /// </remarks>
    internal static void CenterOrigin(SceneNode node)
    {
        if (NodeBounds.LocalCenter(node) is not { } center) return;
        node.EnsurePlacement(center);
    }

    /// <summary>
    /// STL/OBJ carry no units and the slicer treats values as millimetres, but CAD/Blender often
    /// export in metres. If the loaded model's largest dimension is implausibly small
    /// (&lt; <see cref="TinyImportMaxDimMm"/> mm), scale it ×1000 (m→mm) so it isn't an invisible
    /// speck, and warn. Runs only on interactive import — not on workspace restore, whose meshes
    /// were already scaled when first imported.
    /// </summary>
    private static void NormalizeUnitScale(SceneNode node, Action<string>? log)
    {
        var (min, max) = ComputeSubtreeAabb(node);
        if (min.X > max.X) return; // no geometry

        float maxDim = MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
        if (maxDim <= 0f || maxDim >= TinyImportMaxDimMm) return;

        const float factor = 1000f;
        node.LocalTransform = Matrix4.CreateScale(factor) * node.LocalTransform;

        float after = maxDim * factor;
        string msg = $"[import] Model was only {maxDim:0.###} mm — looks like metres exported as mm; " +
                     $"auto-scaled ×1000 to ~{after:0.#} mm (undo to revert, or export in millimetres).";
        if (after < TinyImportMaxDimMm)
            msg += " Still very small after scaling — check the model's size in your CAD.";
        log?.Invoke(msg);
    }

    /// <summary>
    /// Loads a model file and applies <paramref name="localTransform"/> without bed placement.
    /// Used when restoring a saved workspace.
    /// </summary>
    internal static SceneNode? LoadAtTransform(string filePath, Matrix4 localTransform)
    {
        var node = LoadFile(filePath);
        if (node is null) return null;

        node.LocalTransform = localTransform;
        return node;
    }

    /// <summary>
    /// Reloads disk geometry into an existing scene node, preserving its transform and scene identity.
    /// GPU meshes must be released and re-uploaded on the GL thread after this call.
    /// </summary>
    internal static bool TryReloadInto(SceneNode target, string filePath)
    {
        var loaded = LoadFile(filePath);
        if (loaded is null) return false;

        var localTransform = target.LocalTransform;
        var visible        = target.Visible;
        var selectable     = target.Selectable;
        var cullFaces      = target.CullFaces;
        var layerPreview   = target.LayerPreview;
        var pickTier       = target.PickTier;
        var name           = target.Name;
        var overlay        = target.Overlay;

        foreach (var child in target.Children.ToList())
            target.RemoveChild(child);

        target.Mesh        = null;
        target.PendingMesh = null;

        if (loaded.PendingMesh is { } pending)
        {
            target.PendingMesh = pending;
            loaded.PendingMesh = null;
        }

        foreach (var child in loaded.Children.ToList())
        {
            loaded.RemoveChild(child);
            target.AddChild(child);
        }

        target.LocalTransform = localTransform;
        target.Visible        = visible;
        target.Selectable     = selectable;
        target.CullFaces      = cullFaces;
        target.LayerPreview   = layerPreview;
        target.PickTier       = pickTier;
        target.Name           = name;
        target.Overlay        = overlay;
        target.SourceFilePath = Path.GetFullPath(filePath);
        return true;
    }

    private static SceneNode? LoadFile(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var node = ext switch
            {
                ".stl"  => StlLoader.Load(filePath),
                ".obj"  => ObjLoader.Load(filePath),
                ".3mf"  => ThreeMfLoader.Load(filePath),
                ".stp" or ".step" => StepLoader.Load(filePath),
                _       => GltfLoader.Load(filePath),
            };
            node.CullFaces     = false;
            node.SourceFilePath = Path.GetFullPath(filePath);
            return node;
        }
        catch (Exception ex)
        {
            // Surface the reason in the app console — a silent null reads as "nothing happened".
            System.Console.WriteLine($"[import] {Path.GetFileName(filePath)} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Translates <paramref name="node"/> so its bounding-box centre XY aligns with the
    /// cell's import surface centre and its bounding-box min-Z sits on that surface.
    /// LFAM 3 (rotary): centres on <c>bed.origin</c> and scales down to fit the table diameter.
    /// LFAM 2 (rectangular): centres on the print-bed grid footprint.
    /// No-op if no geometry is found or no active cell is loaded.
    /// </summary>
    internal static void PlaceOnBed(SceneNode node, CellConfig? activeCell)
    {
        if (activeCell?.Bed is not { } bed) return;

        var surface = bed.ImportSurfaceCenter(activeCell.Robot.WorldPosition);
        var bedCenter = new Vector3(surface.X, surface.Y, surface.Z);

        if (bed.ImportSurfaceRadiusMm is { } radius)
            ScaleToFitWithinRadius(node, radius * RotaryFitMargin);

        var (min, max) = ComputeSubtreeAabb(node);
        if (min.X > max.X) return;

        var center = (min + max) * 0.5f;

        var lt = node.LocalTransform;
        lt.M41 += bedCenter.X - center.X;
        lt.M42 += bedCenter.Y - center.Y;
        lt.M43 += bedCenter.Z - min.Z;
        node.LocalTransform = lt;
    }

    /// <summary>Uniformly scales <paramref name="node"/> so its XY footprint fits in a circle.</summary>
    private static void ScaleToFitWithinRadius(SceneNode node, float maxRadius)
    {
        var (min, max) = ComputeSubtreeAabb(node);
        if (min.X > max.X) return;

        var c = (min + max) * 0.5f;
        float half = MathF.Max(
            MathF.Max(max.X - c.X, c.X - min.X),
            MathF.Max(max.Y - c.Y, c.Y - min.Y));
        if (half <= maxRadius) return;

        float s = maxRadius / half;
        var pre = node.LocalTransform;
        node.LocalTransform =
            Matrix4.CreateTranslation(c) *
            Matrix4.CreateScale(s) *
            Matrix4.CreateTranslation(-c) *
            pre;
    }

    /// <summary>
    /// Recenters native Y-up metre stand geometry so the wrapper origin sits at the
    /// stand base centre. Matches MassiveCONNECT <c>robots.html</c> (-cx, -minY, -cz).
    /// </summary>
    internal static SceneNode RecenterStandYup(SceneNode nativeRoot)
    {
        var (min, max) = ComputeSubtreeAabb(nativeRoot);
        if (min.X > max.X) return nativeRoot;

        var center = (min + max) * 0.5f;
        var recenter = new SceneNode
        {
            Name           = nativeRoot.Name + "_Recenter",
            LocalTransform = Matrix4.CreateTranslation(-center.X, -min.Y, -center.Z),
            Selectable     = false,
        };

        foreach (var child in nativeRoot.Children.ToList())
        {
            nativeRoot.RemoveChild(child);
            recenter.AddChild(child);
        }

        nativeRoot.AddChild(recenter);
        return nativeRoot;
    }

    // Deleted 2026-08-01: RecenterPivotToBottomCenter and its whole support cast — the subtree
    // transform/mesh snapshots, the restore path, and NodeRecenterAction.
    //
    // It rewrote every vertex so the model's own zero became its bottom centre, then compensated
    // with the root transform. Recenter is a pivot move now, which is four numbers and instant. The
    // bake bought nothing visible — slicing works off world transforms — while costing a GPU
    // re-upload, an undo snapshot of every vertex array, and a bug that left the part correctly
    // positioned but not drawing at all. Nothing had enqueued a recenter job since; this was
    // unreachable code with live-looking tests around it.

    private static MeshData CloneMeshData(MeshData mesh) =>
        new(mesh.Positions.ToArray(), mesh.Normals.ToArray(), mesh.Indices?.ToArray() ?? [], mesh.Name,
            mesh.BaseColor, mesh.Metallic, mesh.Roughness,
            mesh.Uvs?.ToArray(), mesh.Tangents?.ToArray(), mesh.Material);


    private static Vector3 TransformPoint(Vector3 p, Matrix4 m)
        => new(
            p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42,
            p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);

    /// <summary>
    /// World-space bounds of the subtree's real geometry.
    /// </summary>
    /// <remarks>
    /// Authoring overlays are excluded, as <see cref="SceneNode.IsAuthoringOverlay"/> promises. A cut
    /// modifier's plane and the Move Origin box are real child nodes with real geometry extending
    /// well past the part, so counting them made Recenter measure the overlay's bottom instead of the
    /// model's and bake a wildly wrong offset. Note this is a <em>measurement</em> fix only — the
    /// bake itself must still shift every mesh in the subtree, overlays included, or they would be
    /// left behind in the part's old coordinates.
    /// </remarks>
    internal static (Vector3 Min, Vector3 Max) ComputeSubtreeWorldAabb(SceneNode root)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;

        foreach (var n in root.SelfAndDescendants())
        {
            if (n.IsAuthoringOverlay) continue;
            var mesh = n.Mesh?.PickingData ?? n.PendingMesh;
            if (mesh is null || mesh.Positions.Length == 0) continue;

            any = true;
            var world = n.WorldTransform;
            foreach (var p in mesh.Positions)
            {
                var w = TransformPoint(p, world);
                min = Vector3.ComponentMin(min, w);
                max = Vector3.ComponentMax(max, w);
            }
        }

        return any ? (min, max) : (new Vector3(float.MaxValue), new Vector3(float.MinValue));
    }

    // -- AABB ------------------------------------------------------------------

    /// <summary>
    /// Computes the world-space axis-aligned bounding box of all <see cref="MeshData"/>
    /// found in <paramref name="root"/>'s subtree. Works on freshly loaded nodes that
    /// are not yet attached to the scene graph (uses WorldTransform up to root).
    /// Returns (MaxValue, MinValue) when no geometry is found.
    /// </summary>
    internal static (Vector3 Min, Vector3 Max) ComputeSubtreeAabb(SceneNode root)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        Span<Vector3> corners = stackalloc Vector3[8];

        foreach (var n in root.SelfAndDescendants())
        {
            // Overlays out, same reason as ComputeSubtreeWorldAabb — this one drives bed placement
            // and the metres-as-millimetres check, so a cut plane in the subtree would skew both.
            if (n.IsAuthoringOverlay) continue;
            if (n.PendingMesh is not { } mesh) continue;

            var world        = n.WorldTransform;
            var (bMin, bMax) = mesh.LocalBounds;

            corners[0] = new(bMin.X, bMin.Y, bMin.Z); corners[1] = new(bMax.X, bMin.Y, bMin.Z);
            corners[2] = new(bMin.X, bMax.Y, bMin.Z); corners[3] = new(bMax.X, bMax.Y, bMin.Z);
            corners[4] = new(bMin.X, bMin.Y, bMax.Z); corners[5] = new(bMax.X, bMin.Y, bMax.Z);
            corners[6] = new(bMin.X, bMax.Y, bMax.Z); corners[7] = new(bMax.X, bMax.Y, bMax.Z);

            foreach (var p in corners)
            {
                var w = new Vector3(
                    p.X * world.M11 + p.Y * world.M21 + p.Z * world.M31 + world.M41,
                    p.X * world.M12 + p.Y * world.M22 + p.Z * world.M32 + world.M42,
                    p.X * world.M13 + p.Y * world.M23 + p.Z * world.M33 + world.M43);
                min = Vector3.ComponentMin(min, w);
                max = Vector3.ComponentMax(max, w);
            }
        }

        return (min, max);
    }
}