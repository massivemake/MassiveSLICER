using MassiveSlicer.Core.Collision;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Viewport.Scene;
using TkMatrix4 = OpenTK.Mathematics.Matrix4;
using NMatrix = System.Numerics.Matrix4x4;
using NVec3 = System.Numerics.Vector3;

namespace MassiveSlicer.Viewport.Collision;

/// <summary>
/// Builds Core collision models from the live scene graph. MUST run on the UI/GL
/// thread (reads SceneNode.WorldTransform / LocalTransform); the produced models
/// are immutable and thread-safe.
///
/// Link assignment: every mesh node under the robot root belongs to its deepest
/// ancestor joint (joint_1..joint_6) → links L1..L6; nodes under no joint → L0
/// (pedestal/carriage). A mounted tool is parented under joint_6, so its geometry
/// lands in L6 automatically. L7 stays reserved (empty) for a dedicated tool body.
/// Link-local vertices are expressed in the joint node's frame (composition of
/// local transforms up to, excluding, the joint) — pose-invariant by construction.
/// </summary>
public static class CollisionModelExtractor
{
    static readonly string[] JointNames =
        ["joint_1", "joint_2", "joint_3", "joint_4", "joint_5", "joint_6"];

    /// <summary>Max points fed to quickhull per link (after voxel decimation).</summary>
    const int MaxHullInputPoints = 4000;

    /// <summary>Aspect ratio beyond which a link is split into two hulls along its long axis.</summary>
    const float SlabSplitAspect = 2.5f;

    /// <param name="toolNode">Mounted tool scene node (lives at SceneRoot, posed as
    /// <c>toolLocal × flangeWorld</c> each frame) — extracted into L7 when given.</param>
    /// <param name="toolLocal">Tool-mesh → flange transform (<c>_toolMeshMatrix</c>).</param>
    public static RobotCollisionModel? ExtractRobot(
        SceneNode robotRoot,
        IReadOnlyList<JointConfig> jointConfigs,
        IReadOnlyList<TkMatrix4> restPoses,
        Action<string>? log = null,
        SceneNode? toolNode = null,
        TkMatrix4? toolLocal = null)
    {
        // Locate joints.
        var jointNodes = new SceneNode?[6];
        int found = 0;
        for (int i = 0; i < 6; i++)
        {
            jointNodes[i] = robotRoot.FindDescendant(JointNames[i]);
            if (jointNodes[i] is not null) found++;
        }
        if (found == 0)
        {
            log?.Invoke("[collision] robot GLB has no joint_1..joint_6 nodes — collision disabled");
            return null;
        }

        var chainRootNode = jointNodes[0]?.Parent;
        var chainRootWorldInv = TkMatrix4.Identity;
        if (chainRootNode is not null)
        {
            var crw = chainRootNode.WorldTransform;
            TkMatrix4.Invert(crw, out chainRootWorldInv);
        }

        // Gather per-link points in link-local frames.
        var linkPoints = new List<NVec3>[RobotCollisionModel.LinkCount];
        for (int i = 0; i < linkPoints.Length; i++) linkPoints[i] = [];

        foreach (var node in robotRoot.SelfAndDescendants())
        {
            var mesh = node.Mesh?.PickingData ?? node.PendingMesh;
            if (mesh is null || mesh.Positions.Length == 0) continue;

            // Deepest joint ancestor (including the node itself).
            int link = 0;
            SceneNode? anchor = chainRootNode;
            for (var walk = node; walk is not null; walk = walk.Parent)
            {
                int ji = Array.IndexOf(jointNodes, walk);
                if (ji >= 0)
                {
                    link = ji + 1;
                    anchor = walk;
                    break;
                }
            }

            // Compose node → anchor-local transform (excluding the anchor itself).
            var m = TkMatrix4.Identity;
            var w = node;
            bool underAnchor = false;
            while (w is not null)
            {
                if (ReferenceEquals(w, anchor)) { underAnchor = true; break; }
                m *= w.LocalTransform;
                w = w.Parent;
            }
            if (!underAnchor)
            {
                // L0 node outside the chain-root subtree: express via world snapshot.
                m = node.WorldTransform * chainRootWorldInv;
            }

            // Decimate per mesh BEFORE accumulating (bounds-relative cell).
            var (bMin, bMax) = mesh.LocalBounds;
            float cell = MathF.Max((bMax - bMin).Length / 24f, 1e-5f);
            var seen = new HashSet<(int, int, int)>();
            foreach (var p in mesh.Positions)
            {
                var key = ((int)MathF.Floor(p.X / cell),
                           (int)MathF.Floor(p.Y / cell),
                           (int)MathF.Floor(p.Z / cell));
                if (!seen.Add(key)) continue;
                var t = OpenTK.Mathematics.Vector4.TransformRow(
                    new OpenTK.Mathematics.Vector4(p, 1f), m);
                linkPoints[link].Add(new NVec3(t.X, t.Y, t.Z));
            }
        }

        // Mounted tool → L7, vertices in the tool node's local frame.
        if (toolNode is not null)
        {
            foreach (var node in toolNode.SelfAndDescendants())
            {
                var mesh = node.Mesh?.PickingData ?? node.PendingMesh;
                if (mesh is null || mesh.Positions.Length == 0) continue;

                var m = TkMatrix4.Identity;
                for (var w2 = node; w2 is not null && !ReferenceEquals(w2, toolNode); w2 = w2.Parent)
                    m *= w2.LocalTransform;

                var (bMin, bMax) = mesh.LocalBounds;
                float cell = MathF.Max((bMax - bMin).Length / 24f, 1e-5f);
                var seen = new HashSet<(int, int, int)>();
                foreach (var p in mesh.Positions)
                {
                    var key = ((int)MathF.Floor(p.X / cell),
                               (int)MathF.Floor(p.Y / cell),
                               (int)MathF.Floor(p.Z / cell));
                    if (!seen.Add(key)) continue;
                    var t = OpenTK.Mathematics.Vector4.TransformRow(
                        new OpenTK.Mathematics.Vector4(p, 1f), m);
                    linkPoints[7].Add(new NVec3(t.X, t.Y, t.Z));
                }
            }
        }

        // Hull each link (slab-split elongated ones).
        var linkHulls = new IReadOnlyList<ConvexHull>[RobotCollisionModel.LinkCount];
        for (int l = 0; l < linkHulls.Length; l++)
        {
            var pts = linkPoints[l];
            if (pts.Count < 4)
            {
                linkHulls[l] = [];
                if (l >= 1 && l <= 6 && jointNodes[l - 1] is not null)
                    log?.Invoke($"[collision] {RobotCollisionModel.LinkNames[l]}: no mesh geometry");
                continue;
            }

            // Cap hull input.
            IReadOnlyList<NVec3> capped = pts;
            if (pts.Count > MaxHullInputPoints)
            {
                var bounds = Aabb.FromPoints([.. pts]);
                float cell = bounds.Extent.Length() / 48f;
                var dec = Quickhull.VoxelDecimate(pts, cell);
                while (dec.Length > MaxHullInputPoints)
                {
                    cell *= 1.6f;
                    dec = Quickhull.VoxelDecimate(pts, cell);
                }
                capped = dec;
            }

            var box = Aabb.FromPoints([.. capped]);
            var ext = box.Extent;
            float longest = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
            float second = ext.X + ext.Y + ext.Z - longest
                           - MathF.Min(ext.X, MathF.Min(ext.Y, ext.Z));

            if (second > 1e-6f && longest / second > SlabSplitAspect)
            {
                // Split along the long axis at the midpoint → two tighter hulls.
                int axis = ext.X == longest ? 0 : ext.Y == longest ? 1 : 2;
                float mid = axis == 0 ? box.Center.X : axis == 1 ? box.Center.Y : box.Center.Z;
                var lo = new List<NVec3>();
                var hi = new List<NVec3>();
                foreach (var p in capped)
                {
                    float c = axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
                    if (c <= mid) lo.Add(p);
                    if (c >= mid) hi.Add(p);   // shared band keeps the hulls overlapping
                }
                linkHulls[l] = lo.Count >= 4 && hi.Count >= 4
                    ? [Quickhull.Build(lo), Quickhull.Build(hi)]
                    : [Quickhull.Build((List<NVec3>)[.. capped])];
            }
            else
            {
                linkHulls[l] = [Quickhull.Build((List<NVec3>)[.. capped])];
            }

            log?.Invoke($"[collision] {RobotCollisionModel.LinkNames[l]}: " +
                        $"{pts.Count} pts → {linkHulls[l].Sum(h => h.Vertices.Length)} hull verts " +
                        $"({linkHulls[l].Count} hull(s))");
        }

        // Sanity: an arm without geometry on the moving links is useless — fail soft.
        int meshedArmLinks = 0;
        for (int l = 1; l <= 6; l++)
            if (linkHulls[l].Count > 0) meshedArmLinks++;
        if (meshedArmLinks < 3)
        {
            log?.Invoke($"[collision] only {meshedArmLinks} arm links have geometry — collision disabled");
            return null;
        }

        var restN = new NMatrix[6];
        for (int i = 0; i < 6; i++) restN[i] = ToNumerics(restPoses[i]);

        var toolLocalN = toolLocal is { } tl ? ToNumerics(tl) : NMatrix.Identity;
        return new RobotCollisionModel(linkHulls, restN, jointConfigs, toolLocalN);
    }

    /// <summary>
    /// World-space triangle snapshot of every Environment-tier mesh node, excluding
    /// the robot subtree (the robot is tier-marked Environment too) and any subtree
    /// in <paramref name="alsoExclude"/>. UI/GL thread only.
    /// </summary>
    public static EnvironmentCollider ExtractEnvironment(
        SceneNode sceneRoot, SceneNode? robotRootExclude,
        IReadOnlyList<SceneNode>? alsoExclude = null, Action<string>? log = null)
    {
        var verts = new List<NVec3>();
        var names = new List<string>();

        bool IsExcluded(SceneNode n)
        {
            for (var w = n; w is not null; w = w.Parent)
            {
                if (ReferenceEquals(w, robotRootExclude)) return true;
                if (alsoExclude is not null)
                    foreach (var ex in alsoExclude)
                        if (ReferenceEquals(w, ex)) return true;
            }
            return false;
        }

        foreach (var node in sceneRoot.SelfAndDescendants())
        {
            if (node.PickTier != PickTier.Environment) continue;
            if (IsExcluded(node)) continue;
            var mesh = node.Mesh?.PickingData ?? node.PendingMesh;
            if (mesh is null || mesh.Positions.Length == 0) continue;

            var wt = node.WorldTransform;
            var pos = mesh.Positions;
            var idx = mesh.Indices;
            int triCount = idx is not null ? idx.Length / 3 : pos.Length / 3;

            for (int t = 0; t < triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    var p = idx is not null ? pos[idx[3 * t + k]] : pos[3 * t + k];
                    var w4 = OpenTK.Mathematics.Vector4.TransformRow(
                        new OpenTK.Mathematics.Vector4(p, 1f), wt);
                    verts.Add(new NVec3(w4.X, w4.Y, w4.Z));
                }
                names.Add(node.Name);
            }
        }

        log?.Invoke($"[collision] environment: {names.Count:N0} triangles");
        return new EnvironmentCollider([.. verts], [.. names]);
    }

    static NMatrix ToNumerics(in TkMatrix4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);

    /// <summary>OpenTK → Numerics for chain-root matrices captured at validation time.</summary>
    public static NMatrix ToNumericsMatrix(in TkMatrix4 m) => ToNumerics(m);

    /// <summary>
    /// Numerics → OpenTK, the exact inverse of <see cref="ToNumericsMatrix"/>. Field-by-field:
    /// both libraries store row-major with the translation in row 4 and transform row vectors,
    /// so no transpose or convention change is involved. Used to hand a rotation computed in
    /// Core (System.Numerics) to the scene graph (OpenTK).
    /// </summary>
    public static TkMatrix4 ToOpenTkMatrix(in NMatrix m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
}
