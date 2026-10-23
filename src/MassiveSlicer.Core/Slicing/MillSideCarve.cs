using System.Numerics;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Viewer-only subtraction of a mill bit from SDF beads.
/// A flat end keeps its cutting face, so a same-layer stepover stays.
/// A ball end is that cylinder with a sphere on the tip: the nose scoops a round surface
/// and the shank still eats what it touches. The toolpath tip is the bottom of the ball.
/// The nose is stored as swept spheres (capsules) so the shader can cut a smooth scoop.
/// The occupancy grid for a ball is only the shank above the equator.
/// </summary>
public static class MillSideCarve
{
    /// <summary>
    /// One stick-out. <see cref="Axis"/> points from the tip toward the spindle and is unit length.
    /// <see cref="Ball"/> puts a sphere of <see cref="Radius"/> on the tip (center is one radius up the axis).
    /// </summary>
    public readonly record struct Cylinder(
        Vector3 Tip, Vector3 Axis, float Radius, float Length, float TipSlab, bool Ball = false);

    /// <summary>
    /// One swept ball. <see cref="A"/> and <see cref="B"/> are sphere centers (tip + axis * radius),
    /// not the programmed tips. A single point sample uses <see cref="A"/> == <see cref="B"/>.
    /// </summary>
    public readonly record struct Nose(Vector3 A, Vector3 B, float Radius);

    /// <summary>
    /// Shank direction: from the cutter tip back toward the spindle.
    /// <paramref name="flip"/> matches the green preview cylinder when that mesh is reversed.
    /// </summary>
    public static Vector3 ShankDirection(
        Vector3 surfaceNormal,
        float tcpYawDeg,
        float toolheadA,
        float toolheadB,
        float toolheadC,
        bool flip)
    {
        var (_, _, cutter) = KukaOrientation.MillFrame(surfaceNormal, tcpYawDeg, toolheadA, toolheadB, toolheadC);
        var shank = flip ? cutter : -cutter;
        float len2 = shank.LengthSquared();
        return len2 < 1e-10f ? Vector3.UnitZ : shank / MathF.Sqrt(len2);
    }

    /// <summary>
    /// True when <paramref name="point"/> is inside the bit.
    /// A flat end hits only the cylindrical side, past the cutting face.
    /// A ball end hits the sphere on the tip and the cylinder above that sphere.
    /// Nothing below the tip is inside the bit.
    /// </summary>
    public static bool InsideShank(Vector3 point, in Cylinder cylinder)
    {
        if (cylinder.Radius <= 0f || cylinder.Length <= 0f) return false;
        var d = point - cylinder.Tip;
        float axial = Vector3.Dot(d, cylinder.Axis);
        if (axial > cylinder.Length) return false;
        float radial2 = d.LengthSquared() - axial * axial;
        float r2 = cylinder.Radius * cylinder.Radius;
        if (cylinder.Ball)
        {
            if (axial < 0f) return false;
            if (axial > cylinder.Radius)
                return radial2 <= r2;
            float dz = axial - cylinder.Radius;
            return dz * dz + radial2 <= r2;
        }
        if (cylinder.Length <= cylinder.TipSlab || axial <= cylinder.TipSlab) return false;
        return radial2 <= r2;
    }

    /// <summary>
    /// Signed distance to a swept ball. Negative is inside the tool nose.
    /// Matches the capsule the SDF shader marches. Zero is the sphere surface.
    /// </summary>
    public static float CapsuleDistance(Vector3 point, Vector3 centerA, Vector3 centerB, float radius)
    {
        var pa = point - centerA;
        var ba = centerB - centerA;
        float denom = ba.LengthSquared();
        float h = denom > 1e-8f ? Math.Clamp(Vector3.Dot(pa, ba) / denom, 0f, 1f) : 0f;
        return (pa - ba * h).Length() - radius;
    }

    /// <summary>
    /// Drops samples that sit on the same stick as the one just kept.
    /// A direction change is always kept. The last sample is always kept.
    /// </summary>
    public static List<Cylinder> Sample(IReadOnlyList<Cylinder> poses, float spacingMm)
    {
        var kept = new List<Cylinder>(poses.Count);
        if (poses.Count == 0) return kept;
        float spacing2 = MathF.Max(spacingMm, 0.5f);
        spacing2 *= spacing2;
        Vector3 lastTip = default;
        Vector3 lastAxis = default;
        bool any = false;
        for (int i = 0; i < poses.Count; i++)
        {
            var c = poses[i];
            bool last = i == poses.Count - 1;
            bool axisChanged = any && Vector3.Dot(c.Axis, lastAxis) < 0.998f;
            bool far = !any || (c.Tip - lastTip).LengthSquared() >= spacing2;
            if (!last && !axisChanged && !far) continue;
            kept.Add(c);
            lastTip = c.Tip;
            lastAxis = c.Axis;
            any = true;
        }
        return kept;
    }

    /// <summary>
    /// Builds the carve grid for a mill toolpath. Bead points are in the same space as the
    /// SDF shader (placed toolpath point minus <paramref name="origin"/>).
    /// </summary>
    public static MillSideCarveGrid BuildForPath(
        Toolpath toolpath,
        Vector3 origin,
        Vector3? rotaryCenter,
        float rotarySign,
        float radius,
        float length,
        float nominalLayerHeight,
        float toolheadA,
        float toolheadB,
        float toolheadC,
        bool flip,
        bool ball = false)
    {
        var poses = new List<Cylinder>(256);
        var noses = ball ? new List<Nose>(256) : null;
        bool inRun = false;
        bool noseOpen = false;
        Vector3 noseA = default, noseB = default, noseDir = default;
        foreach (var layer in toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (move.Kind != MoveKind.Mill || move.IsWipe)
                {
                    if (noseOpen) CloseNose(noses!, radius, ref noseOpen, ref noseA, ref noseB);
                    inRun = false;
                    continue;
                }
                float a = move.KeyframeToolheadA ?? toolheadA;
                float b = move.KeyframeToolheadB ?? toolheadB;
                float c = move.KeyframeToolheadC ?? toolheadC;
                var n = move.Normal.LengthSquared() > 1e-8f ? move.Normal : layer.PlaneNormal;
                var axis = ShankDirection(n, move.TcpYawDeg, a, b, c, flip);
                float slab = TipSlab(layer, move, nominalLayerHeight);
                if (!inRun)
                {
                    var from = PlacePoint(move.From, move.E1Mm, rotaryCenter, rotarySign) - origin;
                    poses.Add(new Cylinder(from, axis, radius, length, slab, ball));
                    if (ball) OpenNose(from + axis * radius, ref noseOpen, ref noseA, ref noseB);
                    inRun = true;
                }
                var to = PlacePoint(move.To, move.E1Mm, rotaryCenter, rotarySign) - origin;
                poses.Add(new Cylinder(to, axis, radius, length, slab, ball));
                if (ball) ExtendNose(to + axis * radius, radius, noses!, ref noseOpen, ref noseA, ref noseB, ref noseDir);
            }
        }
        if (noseOpen) CloseNose(noses!, radius, ref noseOpen, ref noseA, ref noseB);

        float spacing = Math.Clamp(radius * 0.2f, 2f, 6f);
        var sampled = Sample(poses, spacing);
        if (sampled.Count == 0)
            return MillSideCarveGrid.Empty;

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        // The ball reaches one radius past the tip in every direction around its center,
        // so the grid has to cover two radii, not just the shank beside the beads.
        float pad = ball ? radius * 2f + 2f : radius + 2f;
        foreach (var c in sampled)
        {
            if (!float.IsFinite(c.Tip.X)) continue;
            min = Vector3.Min(min, c.Tip);
            max = Vector3.Max(max, c.Tip);
        }
        if (min.X > max.X)
            return MillSideCarveGrid.Empty;
        min -= new Vector3(pad);
        max += new Vector3(pad);
        var grid = MillSideCarveGrid.Build(sampled, min, max);
        if (ball && noses is { Count: > 0 })
            grid.AttachNoses(noses);
        return grid;
    }

    static void OpenNose(Vector3 center, ref bool open, ref Vector3 a, ref Vector3 b)
    {
        open = true;
        a = center;
        b = center;
    }

    /// <summary>
    /// Extends the current swept ball while the new center stays on the same straight cut.
    /// A lean or a corner starts a new capsule so the scoop follows the path.
    /// </summary>
    static void ExtendNose(Vector3 center, float radius, List<Nose> noses, ref bool open, ref Vector3 a, ref Vector3 b, ref Vector3 dir)
    {
        if (!open)
        {
            OpenNose(center, ref open, ref a, ref b);
            return;
        }
        var step = center - b;
        float step2 = step.LengthSquared();
        if (step2 < 1e-8f) return;
        var stepDir = step / MathF.Sqrt(step2);
        bool hasSpan = (b - a).LengthSquared() > 1e-8f;
        if (hasSpan && Vector3.Dot(stepDir, dir) < 0.9995f)
        {
            noses.Add(new Nose(a, b, radius));
            a = b;
        }
        b = center;
        var span = b - a;
        float span2 = span.LengthSquared();
        dir = span2 > 1e-8f ? span / MathF.Sqrt(span2) : stepDir;
    }

    static void CloseNose(List<Nose> noses, float radius, ref bool open, ref Vector3 a, ref Vector3 b)
    {
        if (!open) return;
        noses.Add(new Nose(a, b, radius));
        open = false;
    }

    static float TipSlab(ToolpathLayer layer, ToolpathMove move, float nominal)
    {
        float lh = layer.Height > 0f ? layer.Height : nominal;
        if (lh < 0.05f) lh = MathF.Max(nominal, 0.05f);
        float nom = nominal > 1e-4f ? nominal : lh;
        float flow = Math.Clamp(lh / nom, 0.05f, 2f);
        bool ratioOfThisGap = MathF.Abs(move.HeightScale - flow) <= 1e-3f;
        float h = ratioOfThisGap ? lh : lh * MathF.Max(move.HeightScale, 0.05f);
        return MathF.Max(h * 0.5f, 0.05f);
    }

    /// <summary>Placed point, with the rotary unwind when a center is supplied.</summary>
    public static Vector3 PlacePoint(Vector3 p, float e1, Vector3? rotaryCenter, float rotarySign)
    {
        if (rotaryCenter is not { } c || float.IsNaN(e1)) return p;
        return ProjectionMill.OnTable(p, e1, c, rotarySign);
    }
}

/// <summary>Occupancy of stick-out cylinders. A set cell is material the bit side removes.</summary>
public sealed class MillSideCarveGrid
{
    public static readonly MillSideCarveGrid Empty = new(Vector3.Zero, 1f, 1, 1, 1, new byte[1]);

    public Vector3 Origin { get; }
    public float Cell { get; }
    public int Nx { get; }
    public int Ny { get; }
    public int Nz { get; }
    public byte[] Cells { get; }
    public int OccupiedCount { get; private set; }
    public int CylinderCount { get; private set; }
    public long BuildMilliseconds { get; private set; }

    /// <summary>Swept-ball index. Empty on a flat end, which keeps the voxel shank only.</summary>
    public bool HasAnalytic => NoseCount > 0 && HeadRg is { Length: > 0 };
    public float HeadCell { get; private set; } = 1f;
    public Vector3 HeadOrigin { get; private set; }
    public int HeadNx { get; private set; }
    public int HeadNy { get; private set; }
    public int HeadNz { get; private set; }
    /// <summary>RG32UI pairs: start index, count. Count <see cref="AnalyticFull"/> means the cell is solid.</summary>
    public uint[]? HeadRg { get; private set; }
    public uint[]? ToolIndex { get; private set; }
    /// <summary>Eight floats per nose: centerA.xyz, radius, centerB.xyz, radius.</summary>
    public float[]? ToolData { get; private set; }
    public int NoseCount { get; private set; }
    public int AnalyticMaxList { get; private set; }
    public int AnalyticFullCells { get; private set; }
    public const int AnalyticCap = 128;
    public const uint AnalyticFull = uint.MaxValue;

    MillSideCarveGrid(Vector3 origin, float cell, int nx, int ny, int nz, byte[] cells)
    {
        Origin = origin;
        Cell = cell;
        Nx = nx;
        Ny = ny;
        Nz = nz;
        Cells = cells;
    }

    public bool IsOccupied(Vector3 p)
    {
        if (Cell <= 0f) return false;
        int ix = (int)MathF.Floor((p.X - Origin.X) / Cell);
        int iy = (int)MathF.Floor((p.Y - Origin.Y) / Cell);
        int iz = (int)MathF.Floor((p.Z - Origin.Z) / Cell);
        if ((uint)ix >= (uint)Nx || (uint)iy >= (uint)Ny || (uint)iz >= (uint)Nz) return false;
        return Cells[ix + Nx * (iy + Ny * iz)] != 0;
    }

    /// <summary>
    /// True when any indexed swept ball contains <paramref name="p"/>.
    /// A cell that overflowed the list is treated as inside, matching the shader.
    /// </summary>
    public bool AnalyticContains(Vector3 p)
    {
        if (!HasAnalytic || HeadRg is null || ToolIndex is null || ToolData is null || HeadCell <= 0f)
            return false;
        int ix = (int)MathF.Floor((p.X - HeadOrigin.X) / HeadCell);
        int iy = (int)MathF.Floor((p.Y - HeadOrigin.Y) / HeadCell);
        int iz = (int)MathF.Floor((p.Z - HeadOrigin.Z) / HeadCell);
        if ((uint)ix >= (uint)HeadNx || (uint)iy >= (uint)HeadNy || (uint)iz >= (uint)HeadNz)
            return false;
        int cell = ix + HeadNx * (iy + HeadNy * iz);
        uint start = HeadRg[cell * 2];
        uint count = HeadRg[cell * 2 + 1];
        if (count == AnalyticFull) return true;
        int n = (int)Math.Min(count, (uint)AnalyticCap);
        for (int k = 0; k < n; k++)
        {
            uint ti = ToolIndex[start + (uint)k];
            int o = (int)ti * 8;
            if ((uint)o >= (uint)(ToolData.Length - 7)) continue;
            var a = new Vector3(ToolData[o], ToolData[o + 1], ToolData[o + 2]);
            float radius = ToolData[o + 3];
            var b = new Vector3(ToolData[o + 4], ToolData[o + 5], ToolData[o + 6]);
            if (MillSideCarve.CapsuleDistance(p, a, b, radius) <= 0f) return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the swept-ball lookup the shader marches. Capsules that cross a cell are listed there,
    /// so a neighboring pass still scoops this bead. The long shank stays on the occupancy grid.
    /// </summary>
    public void AttachNoses(IReadOnlyList<MillSideCarve.Nose> noses)
    {
        if (noses.Count == 0) return;
        var min = Origin;
        var max = Origin + new Vector3(Nx * Cell, Ny * Cell, Nz * Cell);
        var ext = Vector3.Max(max - min, new Vector3(1f));
        float head = 4f;
        int hnx, hny, hnz;
        const int headCap = 1_500_000;
        while (true)
        {
            hnx = Math.Max(1, (int)MathF.Ceiling(ext.X / head));
            hny = Math.Max(1, (int)MathF.Ceiling(ext.Y / head));
            hnz = Math.Max(1, (int)MathF.Ceiling(ext.Z / head));
            if ((long)hnx * hny * hnz <= headCap || head >= 16f) break;
            head *= 1.25f;
        }
        int nCells = hnx * hny * hnz;
        var counts = new int[nCells];
        CoverNoses(noses, min, head, hnx, hny, hnz, (cell, _) => Interlocked.Increment(ref counts[cell]));

        var headRg = new uint[nCells * 2];
        var overflowOf = new int[nCells];
        Array.Fill(overflowOf, -1);
        long slots = 0;
        int trimmed = 0;
        int maxList = 0;
        int overflowN = 0;
        for (int i = 0; i < nCells; i++)
        {
            int n = counts[i];
            if (n > maxList) maxList = n;
            if (n <= 0) continue;
            int keep = n;
            if (n > AnalyticCap)
            {
                keep = AnalyticCap;
                overflowOf[i] = overflowN++;
                trimmed++;
            }
            headRg[i * 2] = (uint)slots;
            headRg[i * 2 + 1] = (uint)keep;
            slots += keep;
        }
        var indices = new uint[Math.Max(slots, 1)];
        var cursor = new int[nCells];
        for (int i = 0; i < nCells; i++)
            cursor[i] = (int)headRg[i * 2];
        int cap = AnalyticCap;
        var heapD = new float[Math.Max(overflowN, 1) * cap];
        var heapI = new uint[Math.Max(overflowN, 1) * cap];
        var heapN = new int[Math.Max(overflowN, 1)];
        var stripes = new object[256];
        for (int s = 0; s < stripes.Length; s++) stripes[s] = new object();
        CoverNoses(noses, min, head, hnx, hny, hnz, (cell, tool) =>
        {
            int ov = overflowOf[cell];
            if (ov >= 0)
            {
                int x = cell % hnx;
                int y = (cell / hnx) % hny;
                int z = cell / (hnx * hny);
                var center = new Vector3(min.X + (x + 0.5f) * head, min.Y + (y + 0.5f) * head, min.Z + (z + 0.5f) * head);
                var nose = noses[(int)tool];
                float dist = MillSideCarve.CapsuleDistance(center, nose.A, nose.B, nose.Radius);
                lock (stripes[ov & 255])
                    HeapConsider(heapD, heapI, heapN, ov, cap, dist, tool);
                return;
            }
            if (headRg[cell * 2 + 1] == 0) return;
            int slot = Interlocked.Increment(ref cursor[cell]) - 1;
            if ((uint)slot < (uint)indices.Length)
                indices[slot] = tool;
        });
        for (int i = 0; i < nCells; i++)
        {
            int ov = overflowOf[i];
            if (ov < 0) continue;
            int kept = heapN[ov];
            int start = (int)headRg[i * 2];
            for (int k = 0; k < kept; k++)
                indices[start + k] = heapI[ov * cap + k];
            headRg[i * 2 + 1] = (uint)kept;
        }

        var data = new float[noses.Count * 8];
        for (int i = 0; i < noses.Count; i++)
        {
            var nose = noses[i];
            int o = i * 8;
            data[o] = nose.A.X; data[o + 1] = nose.A.Y; data[o + 2] = nose.A.Z; data[o + 3] = nose.Radius;
            data[o + 4] = nose.B.X; data[o + 5] = nose.B.Y; data[o + 6] = nose.B.Z; data[o + 7] = nose.Radius;
        }

        HeadCell = head;
        HeadOrigin = min;
        HeadNx = hnx;
        HeadNy = hny;
        HeadNz = hnz;
        HeadRg = headRg;
        ToolIndex = indices;
        ToolData = data;
        NoseCount = noses.Count;
        AnalyticMaxList = maxList;
        AnalyticFullCells = trimmed;
    }

    /// <summary>Max-heap of the closest capsules. The root is the farthest one still kept.</summary>
    static void HeapConsider(float[] dist, uint[] id, int[] fill, int ov, int cap, float d, uint tool)
    {
        int baseAt = ov * cap;
        int n = fill[ov];
        if (n < cap)
        {
            int i = n;
            dist[baseAt + i] = d;
            id[baseAt + i] = tool;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (dist[baseAt + parent] >= dist[baseAt + i]) break;
                SwapHeap(dist, id, baseAt + parent, baseAt + i);
                i = parent;
            }
            fill[ov] = n + 1;
            return;
        }
        if (d >= dist[baseAt]) return;
        dist[baseAt] = d;
        id[baseAt] = tool;
        int node = 0;
        while (true)
        {
            int left = node * 2 + 1;
            int right = left + 1;
            int largest = node;
            if (left < cap && dist[baseAt + left] > dist[baseAt + largest]) largest = left;
            if (right < cap && dist[baseAt + right] > dist[baseAt + largest]) largest = right;
            if (largest == node) break;
            SwapHeap(dist, id, baseAt + node, baseAt + largest);
            node = largest;
        }
    }

    static void SwapHeap(float[] dist, uint[] id, int a, int b)
    {
        (dist[a], dist[b]) = (dist[b], dist[a]);
        (id[a], id[b]) = (id[b], id[a]);
    }

    static void CoverNoses(
        IReadOnlyList<MillSideCarve.Nose> noses,
        Vector3 origin, float head, int nx, int ny, int nz,
        Action<int, uint> mark)
    {
        if (noses.Count >= 64)
        {
            Parallel.For(0, noses.Count,
                () => new List<int>(96),
                (i, _, local) =>
                {
                    CoverOne(noses[i], origin, head, nx, ny, nz, local);
                    int prev = int.MinValue;
                    foreach (var id in local)
                    {
                        if (id == prev) continue;
                        prev = id;
                        mark(id, (uint)i);
                    }
                    return local;
                },
                _ => { });
            return;
        }
        var scratch = new List<int>(96);
        for (int i = 0; i < noses.Count; i++)
        {
            CoverOne(noses[i], origin, head, nx, ny, nz, scratch);
            int prev = int.MinValue;
            foreach (var id in scratch)
            {
                if (id == prev) continue;
                prev = id;
                mark(id, (uint)i);
            }
        }
    }

    static void CoverOne(
        in MillSideCarve.Nose nose,
        Vector3 origin, float head, int nx, int ny, int nz,
        List<int> into)
    {
        into.Clear();
        float radius = nose.Radius;
        if (radius <= 0.05f || !float.IsFinite(nose.A.X) || !float.IsFinite(nose.B.X)) return;
        var span = nose.B - nose.A;
        float len = span.Length();
        float stepLen = Math.Min(head * 0.45f, Math.Max(radius * 0.45f, 0.25f));
        int steps = Math.Max(1, (int)MathF.Ceiling(len / stepLen));
        // Cover the gap between samples, but only cells the swept ball actually touches.
        float reach = radius + stepLen * 0.5f;
        float reach2 = reach * reach;
        int nbr = Math.Max(1, (int)MathF.Ceiling(reach / head));
        for (int s = 0; s <= steps; s++)
        {
            var p = nose.A + span * (s / (float)steps);
            int ix = (int)MathF.Floor((p.X - origin.X) / head);
            int iy = (int)MathF.Floor((p.Y - origin.Y) / head);
            int iz = (int)MathF.Floor((p.Z - origin.Z) / head);
            for (int dz = -nbr; dz <= nbr; dz++)
            for (int dy = -nbr; dy <= nbr; dy++)
            for (int dx = -nbr; dx <= nbr; dx++)
            {
                int x = ix + dx, y = iy + dy, z = iz + dz;
                if ((uint)x >= (uint)nx || (uint)y >= (uint)ny || (uint)z >= (uint)nz) continue;
                float x0 = origin.X + x * head;
                float y0 = origin.Y + y * head;
                float z0 = origin.Z + z * head;
                float cx = Math.Clamp(p.X, x0, x0 + head);
                float cy = Math.Clamp(p.Y, y0, y0 + head);
                float cz = Math.Clamp(p.Z, z0, z0 + head);
                float ddx = p.X - cx, ddy = p.Y - cy, ddz = p.Z - cz;
                if (ddx * ddx + ddy * ddy + ddz * ddz > reach2) continue;
                into.Add(x + nx * (y + ny * z));
            }
        }
        into.Sort();
    }

    public static MillSideCarveGrid Build(IReadOnlyList<MillSideCarve.Cylinder> cylinders, Vector3 min, Vector3 max)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var ext = max - min;
        if (ext.X < 1f) { max.X = min.X + 1f; ext.X = 1f; }
        if (ext.Y < 1f) { max.Y = min.Y + 1f; ext.Y = 1f; }
        if (ext.Z < 1f) { max.Z = min.Z + 1f; ext.Z = 1f; }

        float cell = 1.75f;
        int nx, ny, nz;
        const long cap = 28_000_000;
        while (true)
        {
            nx = Math.Max(1, (int)MathF.Ceiling(ext.X / cell));
            ny = Math.Max(1, (int)MathF.Ceiling(ext.Y / cell));
            nz = Math.Max(1, (int)MathF.Ceiling(ext.Z / cell));
            if ((long)nx * ny * nz <= cap || cell >= 6f) break;
            cell *= 1.25f;
        }
        // Keep the far bound inside the last cell.
        var origin = min;
        var cells = new byte[(long)nx * ny * nz];
        var grid = new MillSideCarveGrid(origin, cell, nx, ny, nz, cells)
        {
            CylinderCount = cylinders.Count,
        };
        if (cylinders.Count > 0)
        {
            var stamper = new Stamper(grid);
            if (cylinders.Count >= 128)
                Parallel.For(0, cylinders.Count, i => stamper.Stamp(cylinders[i]));
            else
            {
                for (int i = 0; i < cylinders.Count; i++)
                    stamper.Stamp(cylinders[i]);
            }
        }
        int occupied = 0;
        for (int i = 0; i < cells.Length; i++)
            if (cells[i] != 0) occupied++;
        grid.OccupiedCount = occupied;
        grid.BuildMilliseconds = started.ElapsedMilliseconds;
        return grid;
    }

    sealed class Stamper
    {
        const int G = 8;
        readonly byte[] _cells;
        readonly byte[] _full;
        readonly int _nx, _ny, _nz, _cxn, _cyn, _czn;
        readonly float _cell;
        readonly Vector3 _origin;

        public Stamper(MillSideCarveGrid g)
        {
            _cells = g.Cells;
            _nx = g.Nx;
            _ny = g.Ny;
            _nz = g.Nz;
            _cell = g.Cell;
            _origin = g.Origin;
            _cxn = (_nx + G - 1) / G;
            _cyn = (_ny + G - 1) / G;
            _czn = (_nz + G - 1) / G;
            _full = new byte[_cxn * _cyn * _czn];
        }

        public void Stamp(MillSideCarve.Cylinder c)
        {
            float axisLen = c.Axis.Length();
            if (axisLen < 1e-6f || c.Radius <= 0.05f) return;
            if (c.Ball ? c.Length <= 0.05f : c.Length <= c.TipSlab) return;
            if (MathF.Abs(axisLen - 1f) > 1e-3f)
                c = c with { Axis = c.Axis / axisLen };

            // Flat: start above the cutting face. Ball: voxels start at the equator;
            // the sphere below that is the analytic nose, not this grid.
            var a = c.Ball ? c.Tip + c.Axis * c.Radius : c.Tip + c.Axis * c.TipSlab;
            var b = c.Tip + c.Axis * c.Length;
            var pad = new Vector3(c.Radius + _cell);
            var lo = Vector3.Min(a, b) - pad;
            var hi = Vector3.Max(a, b) + pad;
            if (!Range(lo.X, hi.X, _origin.X, _nx, out int x0, out int x1)) return;
            if (!Range(lo.Y, hi.Y, _origin.Y, _ny, out int y0, out int y1)) return;
            if (!Range(lo.Z, hi.Z, _origin.Z, _nz, out int z0, out int z1)) return;

            int cx0 = x0 / G, cx1 = x1 / G;
            int cy0 = y0 / G, cy1 = y1 / G;
            int cz0 = z0 / G, cz1 = z1 / G;
            float halfA = 0.5f * _cell * (MathF.Abs(c.Axis.X) + MathF.Abs(c.Axis.Y) + MathF.Abs(c.Axis.Z));
            float r = c.Radius + _cell * 0.35f;
            float r2 = r * r;

            for (int cz = cz0; cz <= cz1; cz++)
            for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int fi = cx + _cxn * (cy + _cyn * cz);
                if (_full[fi] != 0) continue;
                if (CoarseInside(cx, cy, cz, c))
                {
                    FillCoarse(cx, cy, cz);
                    _full[fi] = 1;
                    continue;
                }

                int fx0 = Math.Max(x0, cx * G);
                int fy0 = Math.Max(y0, cy * G);
                int fz0 = Math.Max(z0, cz * G);
                int fx1 = Math.Min(x1, cx * G + G - 1);
                int fy1 = Math.Min(y1, cy * G + G - 1);
                int fz1 = Math.Min(z1, cz * G + G - 1);
                for (int iz = fz0; iz <= fz1; iz++)
                {
                    float wz = _origin.Z + (iz + 0.5f) * _cell;
                    for (int iy = fy0; iy <= fy1; iy++)
                    {
                        float wy = _origin.Y + (iy + 0.5f) * _cell;
                        int row = fx0 + _nx * (iy + _ny * iz);
                        for (int ix = fx0; ix <= fx1; ix++, row++)
                        {
                            if (_cells[row] != 0) continue;
                            float wx = _origin.X + (ix + 0.5f) * _cell;
                            var d = new Vector3(wx, wy, wz) - c.Tip;
                            float axial = d.X * c.Axis.X + d.Y * c.Axis.Y + d.Z * c.Axis.Z;
                            float radial2 = d.LengthSquared() - axial * axial;
                            if (c.Ball)
                            {
                                // The nose is an analytic sphere. Voxels are only the shank above the equator,
                                // and only when the whole cell is past that plane so the stair-step cannot
                                // cover the smooth scoop.
                                if (axial - halfA <= c.Radius || axial - halfA > c.Length) continue;
                                if (radial2 <= r2) _cells[row] = 255;
                            }
                            else
                            {
                                if (axial - halfA <= c.TipSlab || axial - halfA > c.Length) continue;
                                if (radial2 <= r2) _cells[row] = 255;
                            }
                        }
                    }
                }
            }
        }

        bool Range(float lo, float hi, float origin, int n, out int i0, out int i1)
        {
            i0 = (int)MathF.Floor((lo - origin) / _cell);
            i1 = (int)MathF.Floor((hi - origin) / _cell);
            if (i0 < 0) i0 = 0;
            if (i1 >= n) i1 = n - 1;
            return i0 <= i1;
        }

        bool CoarseInside(int cx, int cy, int cz, in MillSideCarve.Cylinder c)
        {
            float x0 = _origin.X + cx * G * _cell;
            float y0 = _origin.Y + cy * G * _cell;
            float z0 = _origin.Z + cz * G * _cell;
            float x1 = x0 + G * _cell;
            float y1 = y0 + G * _cell;
            float z1 = z0 + G * _cell;
            return Corner(x0, y0, z0, c) && Corner(x1, y0, z0, c)
                && Corner(x0, y1, z0, c) && Corner(x1, y1, z0, c)
                && Corner(x0, y0, z1, c) && Corner(x1, y0, z1, c)
                && Corner(x0, y1, z1, c) && Corner(x1, y1, z1, c);
        }

        static bool Corner(float x, float y, float z, in MillSideCarve.Cylinder c)
        {
            var d = new Vector3(x, y, z) - c.Tip;
            float axial = d.X * c.Axis.X + d.Y * c.Axis.Y + d.Z * c.Axis.Z;
            float radial2 = d.LengthSquared() - axial * axial;
            if (c.Ball)
            {
                if (axial <= c.Radius + 0.2f || axial > c.Length - 0.2f) return false;
                return radial2 <= c.Radius * c.Radius;
            }
            if (axial <= c.TipSlab + 0.2f || axial > c.Length - 0.2f) return false;
            return radial2 <= c.Radius * c.Radius;
        }

        void FillCoarse(int cx, int cy, int cz)
        {
            int x0 = cx * G;
            int y0 = cy * G;
            int z0 = cz * G;
            if (x0 >= _nx || y0 >= _ny || z0 >= _nz) return;
            int x1 = Math.Min(_nx - 1, x0 + G - 1);
            int y1 = Math.Min(_ny - 1, y0 + G - 1);
            int z1 = Math.Min(_nz - 1, z0 + G - 1);
            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            {
                int row = x0 + _nx * (y + _ny * z);
                for (int x = x0; x <= x1; x++)
                    _cells[row++] = 255;
            }
        }
    }
}
