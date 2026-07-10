using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Adds Formbound-style alignment tabs and bolt lugs along a planar cut perimeter
/// so two printed halves nest and bolt together.
/// </summary>
public static class CutConnectorBuilder
{
    public sealed class Options
    {
        /// <summary>Spacing between connector sites along the cut perimeter (mm).</summary>
        public float SpacingMm { get; init; } = 40f;

        /// <summary>Alignment tab width along the perimeter (mm).</summary>
        public float TabWidthMm { get; init; } = 12f;

        /// <summary>How far tabs protrude past the cut face (mm).</summary>
        public float TabDepthMm { get; init; } = 8f;

        /// <summary>Tab radial height from the cut perimeter into the solid (mm).</summary>
        public float TabHeightMm { get; init; } = 6f;

        /// <summary>Bolt hole diameter (mm).</summary>
        public float BoltDiameterMm { get; init; } = 6f;

        /// <summary>Bolt lug outer diameter (mm).</summary>
        public float BoltLugDiameterMm { get; init; } = 14f;

        /// <summary>Bolt lug thickness (mm).</summary>
        public float BoltLugThicknessMm { get; init; } = 5f;

        /// <summary>Number of segments for cylindrical features.</summary>
        public int CylinderSegments { get; init; } = 16;

        /// <summary>Minimum corner radius for tab fillets (mm) — at least half bead.</summary>
        public float MinCornerRadiusMm { get; init; } = 3f;
    }

    public sealed record Result(MeshData PositiveWithConnectors, MeshData NegativeWithConnectors, int ConnectorCount);

    /// <summary>
    /// Merge connector solid geometry into both halves.
    /// Interdigitated tabs: even sites → male on A / female guide on B;
    /// odd sites → male on B / female guide on A. Bolt lugs are coplanar on both.
    /// </summary>
    public static Result Apply(
        MeshData positive, MeshData negative,
        IReadOnlyList<IReadOnlyList<Vector3>> cutLoops,
        Vector3 planePoint, Vector3 planeNormal,
        Options? options = null)
    {
        var opt = options ?? new Options();
        var n = planeNormal.Normalized();

        var addA = new MeshAccumulator();
        var addB = new MeshAccumulator();
        int count = 0;

        foreach (var loop in cutLoops)
        {
            if (loop.Count < 3) continue;
            var samples = SampleLoop(loop, opt.SpacingMm);
            if (samples.Count == 0) continue;

            // Centroid for outward radial in the cut plane.
            var centroid = Vector3.Zero;
            foreach (var p in loop) centroid += p;
            centroid /= loop.Count;

            for (int i = 0; i < samples.Count; i++)
            {
                var (pos, tangent) = samples[i];
                var radial = pos - centroid;
                radial -= n * Vector3.Dot(radial, n);
                if (radial.LengthSquared < 1e-8f)
                    radial = Vector3.Cross(n, tangent);
                radial = radial.Normalized();
                // Outward = away from solid interior for a typical outer loop (CCW about n).
                // For outer silhouette, outward is +radial from centroid.
                var outward = radial;
                var binormal = Vector3.Cross(n, tangent).Normalized();
                // Prefer outward pointing away from centroid.
                if (Vector3.Dot(binormal, radial) < 0) binormal = -binormal;

                bool maleOnA = (i % 2) == 0;
                AddAlignmentTab(addA, addB, pos, n, tangent, binormal, outward,
                    maleOnA, opt);
                AddBoltLug(addA, pos, n, tangent, binormal, outward, opt);
                AddBoltLug(addB, pos, n, tangent, binormal, outward, opt);
                count++;
            }
        }

        var meshA = Merge(positive, addA, positive.Name);
        var meshB = Merge(negative, addB, negative.Name);
        return new Result(meshA, meshB, count);
    }

    private static void AddAlignmentTab(
        MeshAccumulator a, MeshAccumulator b,
        Vector3 pos, Vector3 planeN, Vector3 tangent, Vector3 binormal, Vector3 outward,
        bool maleOnA, Options opt)
    {
        // Tab sits on the cut face, extends along ±planeN into one half (male)
        // and a slightly larger hollow guide ring on the other (female, solid C-shape
        // approximated as an open rectangular frame).
        float hw = opt.TabWidthMm * 0.5f;
        float hh = opt.TabHeightMm * 0.5f;
        float depth = opt.TabDepthMm;
        float r = MathF.Max(opt.MinCornerRadiusMm, 1f);

        // Male peg: box centered on cut, extruded +planeN for A or -planeN for B.
        var male = maleOnA ? a : b;
        var female = maleOnA ? b : a;
        var maleDir = maleOnA ? planeN : -planeN;
        var femaleDir = -maleDir;

        // Keep tabs inset slightly from perimeter (into solid along -outward).
        var basePt = pos - outward * (hh * 0.3f);

        AddBox(male, basePt + maleDir * (depth * 0.5f),
            tangent * hw, binormal * hh, maleDir * (depth * 0.5f), r);

        // Female: larger open box (U-guide) on the other side — three walls so peg nests.
        float clearance = MathF.Max(0.4f, r * 0.15f);
        float fhw = hw + clearance;
        float fhh = hh + clearance;
        float fdepth = depth * 0.85f;
        var fCenter = basePt + femaleDir * (fdepth * 0.5f);
        // Three slabs: left, right, back (no front — peg inserts from cut face).
        float wall = MathF.Max(1.5f, r * 0.4f);
        AddBox(female, fCenter + binormal * (fhh + wall * 0.5f),
            tangent * fhw, binormal * (wall * 0.5f), femaleDir * (fdepth * 0.5f), r * 0.5f);
        AddBox(female, fCenter - binormal * (fhh + wall * 0.5f),
            tangent * fhw, binormal * (wall * 0.5f), femaleDir * (fdepth * 0.5f), r * 0.5f);
        AddBox(female, fCenter - outward * (hh * 0.2f) - binormal * 0, // back wall toward solid
            tangent * fhw, binormal * (fhh + wall), femaleDir * (wall * 0.5f), r * 0.5f);
    }

    private static void AddBoltLug(
        MeshAccumulator acc, Vector3 pos, Vector3 planeN, Vector3 tangent, Vector3 binormal,
        Vector3 outward, Options opt)
    {
        // Lug sits outside the silhouette on the cut plane so both halves share the
        // same bolt axis when assembled face-to-face.
        float R = opt.BoltLugDiameterMm * 0.5f;
        float rHole = opt.BoltDiameterMm * 0.5f;
        float thick = opt.BoltLugThicknessMm;
        var center = pos + outward * (R + 1f);

        // Annulus extruded along plane normal (symmetric about cut plane).
        int seg = Math.Max(8, opt.CylinderSegments);
        AddAnnulus(acc, center, planeN, outward, tangent, R, rHole, thick * 0.5f, seg);
    }

    private static void AddBox(MeshAccumulator acc, Vector3 center,
        Vector3 hx, Vector3 hy, Vector3 hz, float cornerRadius)
    {
        // 8 corners of a parallelepiped; ignore fillet for solid fill (path fillet is
        // for print paths — here we keep solid tabs with slightly rounded corners
        // by chamfering extreme corners if radius is large relative to size).
        float cx = hx.Length, cy = hy.Length, cz = hz.Length;
        if (cx < 1e-4f || cy < 1e-4f || cz < 1e-4f) return;
        var ux = hx / cx; var uy = hy / cy; var uz = hz / cz;

        // Soften: shrink by min(cornerRadius, 25% of half-extents) then the outer
        // silhouette is less knife-edged (still a box — good enough for LFAM tabs).
        float s = MathF.Min(cornerRadius * 0.25f, MathF.Min(cx, MathF.Min(cy, cz)) * 0.25f);
        cx = MathF.Max(cx - s, cx * 0.75f);
        cy = MathF.Max(cy - s, cy * 0.75f);
        cz = MathF.Max(cz - s, cz * 0.75f);
        hx = ux * cx; hy = uy * cy; hz = uz * cz;

        var c = new Vector3[8];
        int k = 0;
        foreach (int sx in new[] { -1, 1 })
        foreach (int sy in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
            c[k++] = center + hx * sx + hy * sy + hz * sz;

        // Faces (each as 2 tris). Order for outward normals.
        void Quad(int a, int b, int c0, int d, Vector3 n)
        {
            acc.AddTri(c[a], c[b], c[c0], n);
            acc.AddTri(c[a], c[c0], c[d], n);
        }
        // Index map: sx,sy,sz as bit pattern is messy — explicit:
        // 0:--- 1:--+ 2:-+- 3:-++ 4:+-- 5:+-+ 6:++- 7:+++
        // Rebuild with clear indexing:
        c[0] = center - hx - hy - hz;
        c[1] = center + hx - hy - hz;
        c[2] = center + hx + hy - hz;
        c[3] = center - hx + hy - hz;
        c[4] = center - hx - hy + hz;
        c[5] = center + hx - hy + hz;
        c[6] = center + hx + hy + hz;
        c[7] = center - hx + hy + hz;
        Quad(0, 1, 2, 3, -uz);
        Quad(4, 7, 6, 5, uz);
        Quad(0, 4, 5, 1, -uy);
        Quad(3, 2, 6, 7, uy);
        Quad(0, 3, 7, 4, -ux);
        Quad(1, 5, 6, 2, ux);
    }

    private static void AddAnnulus(MeshAccumulator acc, Vector3 center, Vector3 axis,
        Vector3 refRadial, Vector3 refTan, float R, float rHole, float halfThick, int seg)
    {
        axis = axis.Normalized();
        var u = refRadial - axis * Vector3.Dot(refRadial, axis);
        if (u.LengthSquared < 1e-8f) u = refTan;
        u = u.Normalized();
        var v = Vector3.Cross(axis, u).Normalized();

        for (int i = 0; i < seg; i++)
        {
            float a0 = MathF.Tau * i / seg;
            float a1 = MathF.Tau * (i + 1) / seg;
            var o0 = u * MathF.Cos(a0) + v * MathF.Sin(a0);
            var o1 = u * MathF.Cos(a1) + v * MathF.Sin(a1);

            // Outer ring top/bottom
            var ot0 = center + o0 * R + axis * halfThick;
            var ot1 = center + o1 * R + axis * halfThick;
            var ob0 = center + o0 * R - axis * halfThick;
            var ob1 = center + o1 * R - axis * halfThick;
            var it0 = center + o0 * rHole + axis * halfThick;
            var it1 = center + o1 * rHole + axis * halfThick;
            var ib0 = center + o0 * rHole - axis * halfThick;
            var ib1 = center + o1 * rHole - axis * halfThick;

            // Top annulus
            acc.AddTri(ot0, ot1, it1, axis);
            acc.AddTri(ot0, it1, it0, axis);
            // Bottom
            acc.AddTri(ob0, ib0, ib1, -axis);
            acc.AddTri(ob0, ib1, ob1, -axis);
            // Outer wall
            acc.AddTri(ot0, ob0, ob1, o0);
            acc.AddTri(ot0, ob1, ot1, o0);
            // Inner wall (hole)
            acc.AddTri(it0, it1, ib1, -o0);
            acc.AddTri(it0, ib1, ib0, -o0);
        }
    }

    private static List<(Vector3 Pos, Vector3 Tangent)> SampleLoop(IReadOnlyList<Vector3> loop, float spacing)
    {
        var result = new List<(Vector3, Vector3)>();
        if (loop.Count < 3 || spacing < 1f) return result;
        float perimeter = 0f;
        for (int i = 0; i < loop.Count; i++)
            perimeter += (loop[(i + 1) % loop.Count] - loop[i]).Length;
        int count = Math.Max(2, (int)MathF.Round(perimeter / spacing));
        float step = perimeter / count;
        float acc = 0f;
        float next = step * 0.5f;
        for (int i = 0; i < loop.Count && result.Count < count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            var ab = b - a;
            float seg = ab.Length;
            if (seg < 1e-6f) continue;
            var tan = ab / seg;
            while (next <= acc + seg + 1e-4f && result.Count < count)
            {
                float t = (next - acc) / seg;
                result.Add((a + ab * Math.Clamp(t, 0f, 1f), tan));
                next += step;
            }
            acc += seg;
        }
        return result;
    }

    private static MeshData Merge(MeshData baseMesh, MeshAccumulator extra, string name)
    {
        int baseV = baseMesh.Positions.Length;
        var pos = new Vector3[baseV + extra.Positions.Count];
        var nrm = new Vector3[baseV + extra.Normals.Count];
        Array.Copy(baseMesh.Positions, pos, baseV);
        Array.Copy(baseMesh.Normals, nrm, baseV);
        for (int i = 0; i < extra.Positions.Count; i++)
        {
            pos[baseV + i] = extra.Positions[i];
            nrm[baseV + i] = extra.Normals[i];
        }

        int baseI = baseMesh.Indices?.Length ?? 0;
        var idx = new uint[baseI + extra.Indices.Count];
        if (baseMesh.Indices is { } bi)
            Array.Copy(bi, idx, baseI);
        else
        {
            // Non-indexed base: expand not supported here — assume indexed.
            for (int i = 0; i < baseV; i++) idx[i] = (uint)i;
            baseI = baseV;
            idx = new uint[baseI + extra.Indices.Count];
            for (int i = 0; i < baseV; i++) idx[i] = (uint)i;
        }
        for (int i = 0; i < extra.Indices.Count; i++)
            idx[baseI + i] = (uint)baseV + extra.Indices[i];

        return new MeshData(pos, nrm, idx, name, baseMesh.BaseColor, baseMesh.Metallic, baseMesh.Roughness);
    }

    private sealed class MeshAccumulator
    {
        public List<Vector3> Positions { get; } = [];
        public List<Vector3> Normals { get; } = [];
        public List<uint> Indices { get; } = [];

        public void AddTri(Vector3 a, Vector3 b, Vector3 c, Vector3 n)
        {
            if (n.LengthSquared > 1e-8f) n = n.Normalized();
            uint i = (uint)Positions.Count;
            Positions.Add(a); Positions.Add(b); Positions.Add(c);
            Normals.Add(n); Normals.Add(n); Normals.Add(n);
            Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2);
        }
    }
}
