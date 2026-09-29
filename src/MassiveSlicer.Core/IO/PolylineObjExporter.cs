using System.Globalization;
using System.Numerics;
using System.Text;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// OpenTK row-vector draw frame for a toolpath. Shader is
/// <c>vec4(stored - origin, 1) * LocalTransform</c>. Parent is ignored.
/// </summary>
public readonly record struct PolylineDrawFrame(
    Vector3 Origin,
    float M11, float M21, float M31, float M41,
    float M12, float M22, float M32, float M42,
    float M13, float M23, float M33, float M43)
{
    public static PolylineDrawFrame Identity { get; } = new(
        Vector3.Zero,
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0);
}

/// <summary>
/// Wavefront OBJ polylines for Blender (File &gt; Import &gt; Wavefront).
/// Line elements only — no faces. Units are millimeters, Z up, matching the viewport.
/// </summary>
public static class PolylineObjExporter
{
    public const float GapMm = 0.05f;

    public static Vector3 DrawPoint(Vector3 stored, PolylineDrawFrame frame)
    {
        float x = stored.X - frame.Origin.X;
        float y = stored.Y - frame.Origin.Y;
        float z = stored.Z - frame.Origin.Z;
        return new Vector3(
            x * frame.M11 + y * frame.M21 + z * frame.M31 + frame.M41,
            x * frame.M12 + y * frame.M22 + z * frame.M32 + frame.M42,
            x * frame.M13 + y * frame.M23 + z * frame.M33 + frame.M43);
    }

    /// <summary>
    /// Every move, in execution order, welded into chains. A gap (next From is not
    /// the previous To) starts a new chain so Blender does not invent a chord.
    /// Travels stay in — an imported KRL is the whole program.
    /// </summary>
    public static List<List<Vector3>> Chains(Toolpath toolpath, PolylineDrawFrame frame, float gapMm = GapMm)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        var chains = new List<List<Vector3>>();
        List<Vector3>? chain = null;
        float gap2 = gapMm * gapMm;

        foreach (var layer in toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                var from = DrawPoint(move.From, frame);
                var to = DrawPoint(move.To, frame);
                if (chain is null || chain.Count == 0 || Dist2(chain[^1], from) > gap2)
                {
                    chain = new List<Vector3>(8) { from };
                    chains.Add(chain);
                }

                if (Dist2(chain[^1], to) > 1e-8f)
                    chain.Add(to);
            }
        }

        chains.RemoveAll(c => c.Count < 2);
        return chains;
    }

    public static string Write(string name, IReadOnlyList<IReadOnlyList<Vector3>> chains)
    {
        var sb = new StringBuilder(256 + chains.Sum(c => c.Count) * 48);
        Write(sb, name, chains);
        return sb.ToString();
    }

    public static void Write(StringBuilder sb, string name, IReadOnlyList<IReadOnlyList<Vector3>> chains)
    {
        sb.AppendLine("# MassiveSLICER polyline");
        sb.AppendLine("# units: millimeters");
        sb.AppendLine("# axes: slicer world XYZ, Z up (same as Blender)");
        sb.AppendLine("# Blender: File > Import > Wavefront (.obj)");
        sb.AppendLine("# If the scene unit is meters, set Scale to 0.001 on import.");
        sb.AppendLine("# Object > Convert > Curve turns the edges into a curve.");
        sb.Append("o ").AppendLine(SanitizeName(name));

        int index = 1;
        foreach (var chain in chains)
        {
            if (chain.Count < 2) continue;
            int first = index;
            foreach (var p in chain)
            {
                sb.Append("v ")
                    .Append(p.X.ToString("0.000", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(p.Y.ToString("0.000", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(p.Z.ToString("0.000", CultureInfo.InvariantCulture))
                    .AppendLine();
                index++;
            }

            int count = chain.Count;
            for (int i = 0; i < count - 1; i++)
                sb.Append("l ").Append(first + i).Append(' ').Append(first + i + 1).AppendLine();
        }
    }

    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "toolpath";
        var chars = name.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c is ' ' or '\t' or '\r' or '\n' or '/' or '\\')
                chars[i] = '_';
        }
        return new string(chars);
    }

    static float Dist2(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}
