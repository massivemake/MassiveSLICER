using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Projection finish. Samples the helper shape and casts a ray along its surface
/// angle until the part is hit. The cutter blends that angle with the part normal.
/// </summary>
public static class ProjectionMill
{
    public static Toolpath Generate(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<int> indices,
        ProjectionSetup setup,
        MillSettings mill,
        Action<float>? progress = null)
    {
        var tp = new Toolpath();
        if (positions.Count == 0 || indices.Count < 3) return tp;
        progress?.Invoke(2f);
        var grid = TriGrid.Build(positions, normals, indices);
        progress?.Invoke(8f);

        float step = MathF.Max(0.5f, mill.StepoverMm);
        float leave = mill.FinishAllowanceMm + mill.OffsetDistanceMm;
        float lift = MathF.Max(1f, mill.RapidZMm);
        float influence = Math.Clamp(setup.NormalInfluence, 0f, 1f);
        var samples = Samples(setup, step);

        var layer = new ToolpathLayer(0, setup.Center.Z + MathF.Max(1f, setup.HeightMm))
        {
            PlaneNormal = Vector3.UnitZ,
        };
        Vector3? prev = null;
        Vector3 prevStored = Vector3.UnitZ;
        float prevE1 = float.NaN;
        int n = samples.Count;
        for (int s = 0; s < n; s++)
        {
            if ((s & 31) == 0)
                progress?.Invoke(8f + 90f * s / Math.Max(1, n));
            var (origin, dir) = samples[s];
            if (!grid.Hit(origin, dir, 4000f, out var point, out var face))
            {
                prev = null;
                prevE1 = float.NaN;
                continue;
            }
            var outward = Vector3.Dot(face, dir) < 0f ? face : -face;
            if (outward.LengthSquared() < 1e-8f) outward = -dir;
            else outward = Vector3.Normalize(outward);
            var toolZ = Blend(dir, -outward, influence);
            var stored = -toolZ;
            var at = point + stored * leave;
            float e1 = float.NaN;
            if (setup.UseE1)
                (at, stored, e1) = UnwindToStation(at, stored, setup);
            // The rotary stops at ±180. A cut must not be the move that spins
            // the long way across that limit — that rewind is a retract.
            bool e1Jump = !float.IsNaN(e1) && !float.IsNaN(prevE1) && MathF.Abs(e1 - prevE1) > 90f;
            bool near = !e1Jump && prev is { } held
                && (Vector3.Distance(held, at) <= step * 4f || (setup.UseE1 && !float.IsNaN(e1)));
            if (near && prev is { } fromPt)
                layer.Moves.Add(Mill(fromPt, at, stored, e1));
            else if (prev is { } gap)
            {
                float e1Lift = float.IsNaN(prevE1) ? e1 : prevE1;
                var up = gap + prevStored * lift;
                layer.Moves.Add(Mill(gap, up, prevStored, e1Lift, MoveKind.Travel, hop: true));
                layer.Moves.Add(Mill(up, at + stored * lift, stored, e1, MoveKind.Travel));
                layer.Moves.Add(Mill(at + stored * lift, at, stored, e1, MoveKind.Travel));
            }
            prev = at;
            prevStored = stored;
            prevE1 = e1;
        }
        if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        progress?.Invoke(100f);
        return tp;
    }

    static List<(Vector3 Origin, Vector3 Dir)> Samples(ProjectionSetup setup, float step)
    {
        float height = MathF.Max(step, setup.HeightMm);
        float baseR = MathF.Max(1f, setup.BaseDiameterMm * 0.5f);
        float topR = MathF.Max(1f, setup.TopDiameterMm * 0.5f);
        int rings = Math.Clamp((int)MathF.Ceiling(height / step), 2, 48);
        var axisZ = setup.AxisZ.LengthSquared() > 1e-8f ? Vector3.Normalize(setup.AxisZ) : Vector3.UnitZ;
        var axisX = setup.AxisX - axisZ * Vector3.Dot(setup.AxisX, axisZ);
        if (axisX.LengthSquared() < 1e-8f) axisX = MathF.Abs(axisZ.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        axisX = Vector3.Normalize(axisX);
        var axisY = Vector3.Cross(axisZ, axisX);
        var list = new List<(Vector3, Vector3)>();
        void Add(Vector3 localOrigin, Vector3 localDir)
        {
            var origin = setup.Center + axisX * localOrigin.X + axisY * localOrigin.Y + axisZ * localOrigin.Z;
            var dir = axisX * localDir.X + axisY * localDir.Y + axisZ * localDir.Z;
            if (dir.LengthSquared() < 1e-8f) return;
            list.Add((origin, Vector3.Normalize(dir)));
        }
        switch (setup.Shape)
        {
            case ProjectionShape.Sphere:
                Sphere(new Vector3(0, 0, baseR), baseR, setup.Inward, step, Add);
                break;
            case ProjectionShape.Cube:
                Cube(Vector3.Zero, MathF.Max(baseR * 2f, 2f), height, setup.Inward, step, Add);
                break;
            default:
                Cylinder(Vector3.Zero, baseR, topR, height, setup.Inward, rings, step,
                    setup.SliceDirection == ProjectionSliceDirection.Vertical, Add);
                break;
        }
        return list;
    }

    static void Cylinder(Vector3 center, float baseR, float topR, float height, bool inward,
        int rings, float step, bool vertical, Action<Vector3, Vector3> add)
    {
        float midR = MathF.Max(1f, (baseR + topR) * 0.5f);
        int around = Math.Clamp((int)MathF.Ceiling(MathF.Tau * midR / step), 12, 90);
        float drdz = (topR - baseR) / height;
        if (!vertical)
        {
            for (int i = 0; i < rings; i++)
                for (int a = 0; a < around; a++)
                    Emit(center, baseR, topR, height, rings, around, drdz, inward, i, a, add);
            return;
        }
        for (int a = 0; a < around; a++)
        {
            bool up = (a & 1) == 0;
            for (int k = 0; k < rings; k++)
            {
                int i = up ? k : rings - 1 - k;
                Emit(center, baseR, topR, height, rings, around, drdz, inward, i, a, add);
            }
        }
    }

    static void Emit(Vector3 center, float baseR, float topR, float height, int rings, int around,
        float drdz, bool inward, int i, int a, Action<Vector3, Vector3> add)
    {
        float t = rings == 1 ? 0f : i / (float)(rings - 1);
        float r = baseR + (topR - baseR) * t;
        float z = height * t;
        float ang = a / (float)around * MathF.Tau;
        float c = MathF.Cos(ang), s = MathF.Sin(ang);
        var n = Vector3.Normalize(new Vector3(c, s, -drdz));
        if (inward) n = -n;
        add(center + new Vector3(c * r, s * r, z) + n * 0.5f, n);
    }

    static void Sphere(Vector3 center, float radius, bool inward, float step, Action<Vector3, Vector3> add)
    {
        int lat = Math.Clamp((int)MathF.Ceiling(MathF.PI * radius / step), 6, 36);
        int lon = Math.Clamp((int)MathF.Ceiling(MathF.Tau * radius / step), 8, 72);
        for (int i = 1; i < lat; i++)
        {
            float v = i / (float)lat * MathF.PI;
            float sv = MathF.Sin(v), cv = MathF.Cos(v);
            for (int j = 0; j < lon; j++)
            {
                float u = j / (float)lon * MathF.Tau;
                var radial = new Vector3(sv * MathF.Cos(u), sv * MathF.Sin(u), cv);
                var n = inward ? -radial : radial;
                add(center + radial * (radius + 0.5f), n);
            }
        }
    }

    static void Cube(Vector3 center, float size, float height, bool inward, float step, Action<Vector3, Vector3> add)
    {
        float hx = size * 0.5f;
        float hz = height;
        int nx = Math.Clamp((int)MathF.Ceiling(size / step), 2, 24);
        int nz = Math.Clamp((int)MathF.Ceiling(height / step), 2, 24);
        Face(center, new Vector3(hx, 0, hz * 0.5f), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, size, height, nx, nz, inward, add);
        Face(center, new Vector3(-hx, 0, hz * 0.5f), -Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, size, height, nx, nz, inward, add);
        Face(center, new Vector3(0, hx, hz * 0.5f), Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, size, height, nx, nz, inward, add);
        Face(center, new Vector3(0, -hx, hz * 0.5f), -Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, size, height, nx, nz, inward, add);
    }

    static void Face(Vector3 center, Vector3 faceCenter, Vector3 normal, Vector3 axisU, Vector3 axisV,
        float sizeU, float sizeV, int nu, int nv, bool inward, Action<Vector3, Vector3> add)
    {
        var n = inward ? -normal : normal;
        var origin0 = center + faceCenter;
        for (int j = 0; j < nv; j++)
        {
            float v = (j / (float)Math.Max(1, nv - 1) - 0.5f) * sizeV;
            for (int i = 0; i < nu; i++)
            {
                float u = (i / (float)Math.Max(1, nu - 1) - 0.5f) * sizeU;
                var p = origin0 + axisU * u + axisV * v + n * 0.5f;
                add(p, n);
            }
        }
    }

    sealed class TriGrid
    {
        readonly Vector3[] _a, _b, _c, _n0, _n1, _n2;
        readonly List<int>[] _cells;
        readonly Vector3 _min;
        readonly float _cell;
        readonly int _nx, _ny, _nz;

        TriGrid(Vector3[] a, Vector3[] b, Vector3[] c, Vector3[] n0, Vector3[] n1, Vector3[] n2,
            List<int>[] cells, Vector3 min, float cell, int nx, int ny, int nz)
        {
            _a = a; _b = b; _c = c; _n0 = n0; _n1 = n1; _n2 = n2;
            _cells = cells; _min = min; _cell = cell; _nx = nx; _ny = ny; _nz = nz;
        }

        public static TriGrid Build(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, IReadOnlyList<int> indices)
        {
            int triCount = indices.Count / 3;
            var a = new Vector3[triCount];
            var b = new Vector3[triCount];
            var c = new Vector3[triCount];
            var n0 = new Vector3[triCount];
            var n1 = new Vector3[triCount];
            var n2 = new Vector3[triCount];
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            int n = 0;
            for (int t = 0; t + 2 < indices.Count; t += 3)
            {
                int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                if ((uint)i0 >= positions.Count || (uint)i1 >= positions.Count || (uint)i2 >= positions.Count)
                    continue;
                var pa = positions[i0];
                var pb = positions[i1];
                var pc = positions[i2];
                a[n] = pa; b[n] = pb; c[n] = pc;
                n0[n] = (uint)i0 < normals.Count ? normals[i0] : Vector3.Zero;
                n1[n] = (uint)i1 < normals.Count ? normals[i1] : Vector3.Zero;
                n2[n] = (uint)i2 < normals.Count ? normals[i2] : Vector3.Zero;
                min = Vector3.Min(min, Vector3.Min(pa, Vector3.Min(pb, pc)));
                max = Vector3.Max(max, Vector3.Max(pa, Vector3.Max(pb, pc)));
                n++;
            }
            if (n < triCount)
            {
                Array.Resize(ref a, n); Array.Resize(ref b, n); Array.Resize(ref c, n);
                Array.Resize(ref n0, n); Array.Resize(ref n1, n); Array.Resize(ref n2, n);
            }
            var size = Vector3.Max(max - min, new Vector3(1f));
            int res = Math.Clamp((int)MathF.Cbrt(Math.Max(1, n) / 8f), 6, 36);
            float cell = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / res;
            int nx = Math.Clamp((int)MathF.Ceiling(size.X / cell), 1, 48);
            int ny = Math.Clamp((int)MathF.Ceiling(size.Y / cell), 1, 48);
            int nz = Math.Clamp((int)MathF.Ceiling(size.Z / cell), 1, 48);
            var cells = new List<int>[nx * ny * nz];
            for (int i = 0; i < n; i++)
            {
                var lo = Vector3.Min(a[i], Vector3.Min(b[i], c[i]));
                var hi = Vector3.Max(a[i], Vector3.Max(b[i], c[i]));
                int x0 = ClampCell((lo.X - min.X) / cell, nx);
                int x1 = ClampCell((hi.X - min.X) / cell, nx);
                int y0 = ClampCell((lo.Y - min.Y) / cell, ny);
                int y1 = ClampCell((hi.Y - min.Y) / cell, ny);
                int z0 = ClampCell((lo.Z - min.Z) / cell, nz);
                int z1 = ClampCell((hi.Z - min.Z) / cell, nz);
                for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int id = (z * ny + y) * nx + x;
                    (cells[id] ??= []).Add(i);
                }
            }
            return new TriGrid(a, b, c, n0, n1, n2, cells, min, cell, nx, ny, nz);
        }

        public bool Hit(Vector3 origin, Vector3 dir, float maxDist, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = Vector3.UnitZ;
            if (dir.LengthSquared() < 1e-12f) return false;
            dir = Vector3.Normalize(dir);
            if (!Enter(origin, dir, maxDist, out float tEnter, out float tExit)) return false;
            var p = origin + dir * (tEnter + 1e-4f);
            int ix = ClampCell((p.X - _min.X) / _cell, _nx);
            int iy = ClampCell((p.Y - _min.Y) / _cell, _ny);
            int iz = ClampCell((p.Z - _min.Z) / _cell, _nz);
            int stepX = dir.X > 0f ? 1 : dir.X < 0f ? -1 : 0;
            int stepY = dir.Y > 0f ? 1 : dir.Y < 0f ? -1 : 0;
            int stepZ = dir.Z > 0f ? 1 : dir.Z < 0f ? -1 : 0;
            float tDeltaX = stepX == 0 ? float.PositiveInfinity : _cell / MathF.Abs(dir.X);
            float tDeltaY = stepY == 0 ? float.PositiveInfinity : _cell / MathF.Abs(dir.Y);
            float tDeltaZ = stepZ == 0 ? float.PositiveInfinity : _cell / MathF.Abs(dir.Z);
            float tMaxX = NextBoundary(origin.X, dir.X, _min.X, ix, stepX);
            float tMaxY = NextBoundary(origin.Y, dir.Y, _min.Y, iy, stepY);
            float tMaxZ = NextBoundary(origin.Z, dir.Z, _min.Z, iz, stepZ);
            float best = maxDist;
            bool any = false;
            int guard = _nx + _ny + _nz + 3;
            while (guard-- > 0 && tEnter <= tExit)
            {
                int id = (iz * _ny + iy) * _nx + ix;
                if ((uint)id < (uint)_cells.Length && _cells[id] is { } bucket)
                {
                    foreach (int i in bucket)
                    {
                        if (!RayTriangle(origin, dir, _a[i], _b[i], _c[i], out float dist, out float u, out float v))
                            continue;
                        if (dist <= 0.2f || dist >= best) continue;
                        float w = 1f - u - v;
                        var nn = _n0[i] * w + _n1[i] * u + _n2[i] * v;
                        if (nn.LengthSquared() < 1e-10f)
                            nn = Vector3.Cross(_b[i] - _a[i], _c[i] - _a[i]);
                        // A face whose normal travels with the ray is the back of the triangle.
                        if (nn.LengthSquared() < 1e-10f || Vector3.Dot(Vector3.Normalize(nn), dir) >= -1e-3f)
                            continue;
                        best = dist;
                        point = origin + dir * dist;
                        normal = Vector3.Normalize(nn);
                        any = true;
                    }
                }
                float next = MathF.Min(tMaxX, MathF.Min(tMaxY, tMaxZ));
                if (any && best <= next) return true;
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
                {
                    ix += stepX; tEnter = tMaxX; tMaxX += tDeltaX;
                    if ((uint)ix >= (uint)_nx) break;
                }
                else if (tMaxY <= tMaxZ)
                {
                    iy += stepY; tEnter = tMaxY; tMaxY += tDeltaY;
                    if ((uint)iy >= (uint)_ny) break;
                }
                else
                {
                    iz += stepZ; tEnter = tMaxZ; tMaxZ += tDeltaZ;
                    if ((uint)iz >= (uint)_nz) break;
                }
            }
            return any;
        }

        float NextBoundary(float origin, float dir, float min, int i, int step)
        {
            if (step == 0 || MathF.Abs(dir) < 1e-12f) return float.PositiveInfinity;
            float edge = min + (step > 0 ? i + 1 : i) * _cell;
            return (edge - origin) / dir;
        }

        bool Enter(Vector3 origin, Vector3 dir, float maxDist, out float t, out float tExit)
        {
            var max = _min + new Vector3(_nx * _cell, _ny * _cell, _nz * _cell);
            t = 0f;
            tExit = maxDist;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
                float d = axis == 0 ? dir.X : axis == 1 ? dir.Y : dir.Z;
                float mn = axis == 0 ? _min.X : axis == 1 ? _min.Y : _min.Z;
                float mx = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
                if (MathF.Abs(d) < 1e-8f)
                {
                    if (o < mn || o > mx) return false;
                    continue;
                }
                float t1 = (mn - o) / d;
                float t2 = (mx - o) / d;
                if (t1 > t2) (t1, t2) = (t2, t1);
                t = MathF.Max(t, t1);
                tExit = MathF.Min(tExit, t2);
                if (t > tExit) return false;
            }
            if (t < 0f) t = 0f;
            return tExit >= t;
        }

        static int ClampCell(float v, int n)
            => v < 0f ? 0 : v >= n ? n - 1 : (int)v;
    }

    static bool RayTriangle(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, Vector3 c,
        out float dist, out float u, out float v)
    {
        dist = u = v = 0f;
        var ab = b - a;
        var ac = c - a;
        var p = Vector3.Cross(dir, ac);
        float det = Vector3.Dot(ab, p);
        if (MathF.Abs(det) < 1e-8f) return false;
        float inv = 1f / det;
        var tvec = origin - a;
        u = Vector3.Dot(tvec, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(tvec, ab);
        v = Vector3.Dot(dir, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        dist = Vector3.Dot(ac, q) * inv;
        return dist > 0f;
    }

    /// <summary>LFAM 3 rotary soft limit. E1 outside this range does not reach the table.</summary>
    public const float RotaryLimitDeg = 180f;

    static (Vector3 Point, Vector3 Normal, float E1Deg) UnwindToStation(
        Vector3 point, Vector3 normal, ProjectionSetup setup)
    {
        var c = setup.RotaryCenter;
        float ang = MathF.Atan2(point.Y - c.Y, point.X - c.X);
        var ax = setup.AxisX;
        float station = ax.X * ax.X + ax.Y * ax.Y > 1e-6f ? MathF.Atan2(ax.Y, ax.X) : 0f;
        float delta = station - ang;
        while (delta > MathF.PI) delta -= MathF.Tau;
        while (delta < -MathF.PI) delta += MathF.Tau;
        float sign = MathF.Abs(setup.RotarySign) < 0.1f ? -1f : MathF.Sign(setup.RotarySign);
        // Principal angle only. Continuing past ±180 is the same pose one turn
        // earlier, and the table cannot pass the stop.
        float e1 = delta * (180f / MathF.PI) / sign;
        if (e1 > RotaryLimitDeg) e1 -= 360f;
        if (e1 < -RotaryLimitDeg) e1 += 360f;
        return (RotateZ(point, c, delta), RotateZ(normal, Vector3.Zero, delta), e1);
    }

    /// <summary>
    /// Point on the part at the pose the path was generated in. Station points are
    /// where the cutter meets the work after E1 has spun that spot to the arm.
    /// Drawing uses this so the line sits on the mesh and can spin with the table.
    /// </summary>
    public static Vector3 OnTable(Vector3 stationPoint, float e1Deg, Vector3 rotaryCenter, float rotarySign)
    {
        if (float.IsNaN(e1Deg)) return stationPoint;
        float sign = MathF.Abs(rotarySign) < 0.1f ? -1f : MathF.Sign(rotarySign);
        float delta = e1Deg * sign * (MathF.PI / 180f);
        return RotateZ(stationPoint, rotaryCenter, -delta);
    }

    static Vector3 RotateZ(Vector3 p, Vector3 center, float rad)
    {
        float cs = MathF.Cos(rad), sn = MathF.Sin(rad);
        float x = p.X - center.X, y = p.Y - center.Y;
        return new Vector3(center.X + cs * x - sn * y, center.Y + sn * x + cs * y, p.Z);
    }

    static ToolpathMove Mill(Vector3 from, Vector3 to, Vector3 normal, float e1,
        MoveKind kind = MoveKind.Mill, bool hop = false)
        => new(from, to, kind) { Normal = normal, E1Mm = e1, IsZHop = hop };

    static Vector3 Blend(Vector3 shapeDir, Vector3 surfaceDir, float influence)
    {
        var v = shapeDir * (1f - influence) + surfaceDir * influence;
        return v.LengthSquared() < 1e-8f ? Vector3.Normalize(shapeDir) : Vector3.Normalize(v);
    }
}
