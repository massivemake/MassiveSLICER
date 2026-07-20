using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Collision;

/// <summary>
/// Deposited print material as per-move oriented boxes in an XY hash grid.
/// Each extrude move becomes one OBB carrying its flat move index, so
/// "material printed before move N" is a simple index filter — no per-move rebuilds.
/// Built directly from the <see cref="Toolpath"/> (world/sliced space; the caller
/// applies any node transform to the ROBOT side, not to the beads — both sides of
/// the check must share the toolpath's coordinate frame).
/// </summary>
public sealed class BeadObstacleGrid
{
    readonly struct Bead
    {
        public readonly Vector3 Center, Ax0, Ax1, Ax2, Half;
        public readonly Aabb Bounds;
        public readonly int FlatIndex;

        public Bead(Vector3 center, Vector3 ax0, Vector3 ax1, Vector3 ax2,
                    Vector3 half, int flatIndex)
        {
            Center = center; Ax0 = ax0; Ax1 = ax1; Ax2 = ax2; Half = half;
            FlatIndex = flatIndex;
            var support = new ObbSupport(center, ax0, ax1, ax2, half);
            Bounds = support.Bounds;
        }
    }

    readonly List<Bead> _beads = [];
    readonly Dictionary<(int, int), List<int>> _grid = [];
    readonly float _cell;

    public int BeadCount => _beads.Count;

    public BeadObstacleGrid(Toolpath toolpath, float beadWidthMm)
        : this(toolpath, beadWidthMm, Matrix4x4.Identity, Vector3.Zero) { }

    /// <param name="world">Node world transform — stored toolpath positions are mapped
    /// as <c>(pos − origin) · world</c>, matching the validation target math, so bead
    /// obstacles land in the same world frame as the posed robot hulls.</param>
    public BeadObstacleGrid(Toolpath toolpath, float beadWidthMm,
                            in Matrix4x4 world, Vector3 origin)
    {
        _cell = MathF.Max(beadWidthMm * 4f, 1f);
        bool identity = world.IsIdentity && origin == Vector3.Zero;

        int flat = 0;
        foreach (var layer in toolpath.Layers)
        {
            var upRaw = layer.PlaneNormal.LengthSquared() > 1e-8f
                ? Vector3.Normalize(layer.PlaneNormal)
                : Vector3.UnitZ;
            var up = identity ? upRaw : Vector3.Normalize(Vector3.TransformNormal(upRaw, world));
            float layerH = layer.Height > 0f ? layer.Height : 3f;

            foreach (var mv in layer.Moves)
            {
                int flatIdx = flat++;
                if (!ToolpathMoveKinds.IsCutSegment(mv.Kind) || mv.IsWipe) continue;

                var from = identity ? mv.From : Vector3.Transform(mv.From - origin, world);
                var to   = identity ? mv.To   : Vector3.Transform(mv.To - origin, world);
                var seg = to - from;
                float len = seg.Length();
                if (len < 1e-4f) continue;
                var ax0 = seg / len;

                // Right vector: perpendicular to both path and stacking direction.
                var ax1 = Vector3.Cross(up, ax0);
                if (ax1.LengthSquared() < 1e-8f)
                    ax1 = Vector3.Cross(Vector3.UnitX, ax0);
                if (ax1.LengthSquared() < 1e-8f)
                    ax1 = Vector3.Cross(Vector3.UnitY, ax0);
                ax1 = Vector3.Normalize(ax1);
                var ax2 = Vector3.Normalize(Vector3.Cross(ax0, ax1));

                float halfH = layerH * MathF.Max(mv.HeightScale, 0.05f) * 0.5f;
                var bead = new Bead(
                    (from + to) * 0.5f,
                    ax0, ax1, ax2,
                    new Vector3(len * 0.5f, beadWidthMm * 0.5f, halfH),
                    flatIdx);

                int idx = _beads.Count;
                _beads.Add(bead);

                int x0 = (int)MathF.Floor(bead.Bounds.Min.X / _cell);
                int x1 = (int)MathF.Floor(bead.Bounds.Max.X / _cell);
                int y0 = (int)MathF.Floor(bead.Bounds.Min.Y / _cell);
                int y1 = (int)MathF.Floor(bead.Bounds.Max.Y / _cell);
                for (int gx = x0; gx <= x1; gx++)
                    for (int gy = y0; gy <= y1; gy++)
                    {
                        if (!_grid.TryGetValue((gx, gy), out var list))
                            _grid[(gx, gy)] = list = [];
                        list.Add(idx);
                    }
            }
        }
    }

    public ObbSupport Obb(int idx)
    {
        var b = _beads[idx];
        return new ObbSupport(b.Center, b.Ax0, b.Ax1, b.Ax2, b.Half);
    }

    public int FlatIndex(int idx) => _beads[idx].FlatIndex;

    /// <summary>
    /// Appends bead indices overlapping <paramref name="box"/> that were deposited
    /// before <paramref name="maxFlatIdx"/>, are outside the nozzle-exclusion radius
    /// around <paramref name="tcp"/>, AND whose top rises above
    /// <paramref name="minTopZ"/> — material at/below the nozzle plane is never an
    /// obstacle (the extruder always hovers just above what it printed).
    /// Thread-safe: caller supplies scratch.
    /// </summary>
    public void Query(in Aabb box, int maxFlatIdx, Vector3 tcp, float nozzleExcludeR,
                      float minTopZ, List<int> hits, HashSet<int> seenScratch)
    {
        seenScratch.Clear();
        float r2 = nozzleExcludeR * nozzleExcludeR;

        int x0 = (int)MathF.Floor(box.Min.X / _cell);
        int x1 = (int)MathF.Floor(box.Max.X / _cell);
        int y0 = (int)MathF.Floor(box.Min.Y / _cell);
        int y1 = (int)MathF.Floor(box.Max.Y / _cell);

        for (int gx = x0; gx <= x1; gx++)
            for (int gy = y0; gy <= y1; gy++)
            {
                if (!_grid.TryGetValue((gx, gy), out var list)) continue;
                foreach (var idx in list)
                {
                    if (!seenScratch.Add(idx)) continue;
                    var b = _beads[idx];
                    if (b.FlatIndex > maxFlatIdx) continue;
                    if (b.Bounds.Max.Z <= minTopZ) continue;   // at/below nozzle plane
                    if (Vector3.DistanceSquared(b.Center, tcp) < r2) continue;
                    if (!b.Bounds.Overlaps(box)) continue;
                    hits.Add(idx);
                }
            }
    }
}
