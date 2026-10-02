using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Imports a desktop slicer toolpath (Bambu / Orca / Prusa gcode, or a
/// <c>.gcode.3mf</c> plate) that was sliced at a reduced scale, and rebuilds
/// it at full size for MassiveDRIVE. Machine prime lines and nozzle wipes
/// are not part of the part.
/// </summary>
public static class ScaledGcodeImporter
{
    public const float DefaultSourcePercent = 10f;

    public readonly record struct Result(
        Toolpath Toolpath,
        int MoveCount,
        int LayerCount,
        float ScaleFactor,
        float BeadWidthMm,
        float LayerHeightMm,
        Vector3 SizeMm);

    public static bool IsSupportedPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".gcode.3mf", StringComparison.OrdinalIgnoreCase))
            return true;
        var ext = Path.GetExtension(name);
        return ext.Equals(".gcode", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gco", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".3mf", StringComparison.OrdinalIgnoreCase);
    }

    public static string ReadGcodeText(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".gcode.3mf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase))
            return ReadPlateGcode(path);
        return File.ReadAllText(path);
    }

    public static string ReadPlateGcode(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var plates = zip.Entries
            .Where(e => e.FullName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase)
                        && !e.FullName.EndsWith(".gcode.md5", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.FullName.Contains("plate_1", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.Length)
            .ToList();
        if (plates.Count == 0)
            throw new InvalidDataException("No plate gcode inside this 3mf.");
        using var reader = new StreamReader(plates[0].Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// <paramref name="sourcePercent"/> is how the desktop file was sliced (10 = 10%).
    /// Geometry is scaled by 100 / that percent, then centered on
    /// <paramref name="bedWorldOffset"/> (Print Bed 0,0,0). Z stays nozzle height
    /// above that bed.
    /// </summary>
    public static Result Import(string gcode, float sourcePercent, Vector3 bedWorldOffset)
    {
        if (sourcePercent is <= 0.01f or > 100f)
            throw new ArgumentOutOfRangeException(nameof(sourcePercent), "Source scale must be between 0.01 and 100 percent.");
        float factor = 100f / sourcePercent;

        var raw = Parse(gcode);
        if (raw.Moves.Count == 0)
            return new Result(new Toolpath(), 0, 0, factor, 0, 0, Vector3.Zero);

        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        foreach (var m in raw.Moves)
        {
            if (!m.Extrude) continue;
            Acc(m.From);
            Acc(m.To);
        }
        if (float.IsPositiveInfinity(minX))
        {
            foreach (var m in raw.Moves)
            {
                Acc(m.From);
                Acc(m.To);
            }
        }

        float cx = (minX + maxX) * 0.5f;
        float cy = (minY + maxY) * 0.5f;
        Vector3 Map(Vector3 p) => new(
            (p.X - cx) * factor + bedWorldOffset.X,
            (p.Y - cy) * factor + bedWorldOffset.Y,
            p.Z * factor + bedWorldOffset.Z);

        var tp = new Toolpath();
        ToolpathLayer? layer = null;
        int layerIndex = -1;
        int moves = 0;
        foreach (var m in raw.Moves)
        {
            if (layer is null || m.Layer != layerIndex)
            {
                layerIndex = m.Layer;
                float z = Map(m.To).Z;
                layer = new ToolpathLayer(tp.Layers.Count, z)
                {
                    Height = raw.LayerHeightMm * factor,
                    PlaneNormal = Vector3.UnitZ,
                };
                tp.Layers.Add(layer);
            }

            var move = new ToolpathMove(Map(m.From), Map(m.To), m.Extrude ? MoveKind.Extrude : MoveKind.Travel)
            {
                IsLayerChange = m.LayerChange && !m.Extrude,
                IsZHop = m.ZHop,
                IsBrim = m.Brim,
                Normal = Vector3.UnitZ,
            };
            layer.Moves.Add(move);
            moves++;
        }

        var size = new Vector3((maxX - minX) * factor, (maxY - minY) * factor, (maxZ - minZ) * factor);
        return new Result(
            tp,
            moves,
            tp.Layers.Count,
            factor,
            Math.Max(0.1f, raw.BeadWidthMm * factor),
            Math.Max(0.1f, raw.LayerHeightMm * factor),
            size);

        void Acc(Vector3 p)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Z < minZ) minZ = p.Z;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
            if (p.Z > maxZ) maxZ = p.Z;
        }
    }

    sealed class Raw
    {
        public List<RawMove> Moves { get; } = [];
        public float BeadWidthMm = 0.4f;
        public float LayerHeightMm = 0.2f;
    }

    readonly record struct RawMove(
        Vector3 From,
        Vector3 To,
        bool Extrude,
        bool LayerChange,
        bool ZHop,
        bool Brim,
        int Layer);

    static bool IsToolpathEnd(string s)
        => s.StartsWith("; MACHINE_END_GCODE_START", StringComparison.OrdinalIgnoreCase)
           || s.StartsWith(";EXECUTABLE_BLOCK_END", StringComparison.OrdinalIgnoreCase)
           || s.StartsWith("; EXECUTABLE_BLOCK_END", StringComparison.OrdinalIgnoreCase);

    static Raw Parse(string gcode)
    {
        var raw = new Raw();
        if (string.IsNullOrEmpty(gcode))
            return raw;

        bool absXyz = true;
        bool absE = false;
        float x = 0, y = 0, z = 0, e = 0;
        int motion = 1;
        bool started = false;
        bool inWipe = false;
        bool primed = false;
        bool brim = false;
        int layer = 0;
        bool layerChangePending = false;
        bool haveBead = false;
        Vector3? last = null;

        foreach (var line in gcode.Split('\n'))
        {
            var s = line.Trim();
            if (s.Length == 0)
                continue;
            if (s[0] == ';')
            {
                NoteComment(s, raw, ref started, ref inWipe, ref brim, ref layer, ref layerChangePending, ref haveBead);
                if (IsToolpathEnd(s))
                    break;
                continue;
            }

            int semi = s.IndexOf(';');
            if (semi >= 0)
                s = s[..semi].Trim();
            if (s.Length == 0)
                continue;

            var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                continue;
            var cmd = words[0].ToUpperInvariant();
            if (cmd is "G90") { absXyz = true; continue; }
            if (cmd is "G91") { absXyz = false; continue; }
            if (cmd is "M82") { absE = true; continue; }
            if (cmd is "M83") { absE = false; continue; }
            if (cmd is "G92")
            {
                ApplySet(words, ref x, ref y, ref z, ref e);
                continue;
            }
            if (cmd is "G0" or "G00") motion = 0;
            else if (cmd is "G1" or "G01") motion = 1;
            else if (cmd is "G2" or "G02") motion = 2;
            else if (cmd is "G3" or "G03") motion = 3;
            else if (cmd.StartsWith('G') || cmd.StartsWith('M') || cmd.StartsWith('T'))
                continue;
            else
                words = new[] { motion == 0 ? "G0" : "G1" }.Concat(words).ToArray();

            if (!started)
            {
                // Keep the machine pose through the prime line, but do not draw it.
                ApplyMotionPose(words, absXyz, absE, ref x, ref y, ref z, ref e);
                primed = true;
                continue;
            }
            if (!primed)
                primed = true;

            float x0 = x, y0 = y, z0 = z, e0 = e;
            float iOff = 0, jOff = 0, r = float.NaN;
            bool hasI = false, hasJ = false, hasR = false;
            foreach (var w in words.Skip(1))
            {
                if (w.Length < 2) continue;
                char a = char.ToUpperInvariant(w[0]);
                if (!float.TryParse(w.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    continue;
                switch (a)
                {
                    case 'X': x = absXyz ? v : x + v; break;
                    case 'Y': y = absXyz ? v : y + v; break;
                    case 'Z': z = absXyz ? v : z + v; break;
                    case 'E': e = absE ? v : e + v; break;
                    case 'I': iOff = v; hasI = true; break;
                    case 'J': jOff = v; hasJ = true; break;
                    case 'R': r = v; hasR = true; break;
                }
            }

            if (inWipe)
                continue;

            bool extrude = motion != 0 && (absE ? e > e0 + 1e-6f : e > e0 + 1e-6f);
            // e was already updated; compare against e0.
            var from = new Vector3(x0, y0, z0);
            var to = new Vector3(x, y, z);
            bool zOnly = MathF.Abs(x - x0) < 1e-4f && MathF.Abs(y - y0) < 1e-4f && MathF.Abs(z - z0) > 1e-4f;

            if (motion is 2 or 3 && (hasI || hasJ || hasR))
            {
                EmitArc(raw, from, to, iOff, jOff, hasR ? r : float.NaN, motion == 2, extrude && !inWipe,
                    layerChangePending, zOnly, brim, layer, ref last);
                layerChangePending = false;
                continue;
            }

            if ((to - from).LengthSquared() < 1e-8f)
                continue;

            Emit(raw, from, to, extrude, layerChangePending && !extrude, zOnly && !extrude, brim, layer, ref last);
            layerChangePending = false;
        }

        return raw;
    }

    static void NoteComment(
        string s,
        Raw raw,
        ref bool started,
        ref bool inWipe,
        ref bool brim,
        ref int layer,
        ref bool layerChangePending,
        ref bool haveBead)
    {
        if (s.Contains("WIPE_START", StringComparison.OrdinalIgnoreCase))
            inWipe = true;
        else if (s.Contains("WIPE_END", StringComparison.OrdinalIgnoreCase))
            inWipe = false;

        if (s.Contains("CHANGE_LAYER", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith(";LAYER_CHANGE", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith(";LAYER:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("; LAYER:", StringComparison.OrdinalIgnoreCase))
        {
            if (started)
            {
                layer++;
                layerChangePending = true;
            }
            started = true;
        }

        if (TryCommentNumber(s, "LINE_WIDTH:", out float width) && width > 0.05f && !haveBead)
        {
            raw.BeadWidthMm = width;
            haveBead = true;
        }
        if (TryCommentNumber(s, "LAYER_HEIGHT:", out float lh) && lh > 0.01f)
            raw.LayerHeightMm = lh;
        if (TryCommentNumber(s, "Z_HEIGHT:", out float zh) && zh > 0.01f && raw.LayerHeightMm <= 0.01f)
            raw.LayerHeightMm = zh;

        if (s.Contains("FEATURE:", StringComparison.OrdinalIgnoreCase))
            brim = s.Contains("Brim", StringComparison.OrdinalIgnoreCase);
    }

    static bool TryCommentNumber(string line, string key, out float value)
    {
        value = 0;
        int i = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        var rest = line[(i + key.Length)..].Trim();
        int cut = rest.IndexOfAny(new[] { ' ', '\t', ';' });
        if (cut > 0) rest = rest[..cut];
        return float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    static void ApplySet(string[] words, ref float x, ref float y, ref float z, ref float e)
    {
        foreach (var w in words.Skip(1))
        {
            if (w.Length < 2) continue;
            if (!float.TryParse(w.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                continue;
            switch (char.ToUpperInvariant(w[0]))
            {
                case 'X': x = v; break;
                case 'Y': y = v; break;
                case 'Z': z = v; break;
                case 'E': e = v; break;
            }
        }
    }

    static void ApplyMotionPose(string[] words, bool absXyz, bool absE, ref float x, ref float y, ref float z, ref float e)
    {
        foreach (var w in words.Skip(1))
        {
            if (w.Length < 2) continue;
            if (!float.TryParse(w.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                continue;
            switch (char.ToUpperInvariant(w[0]))
            {
                case 'X': x = absXyz ? v : x + v; break;
                case 'Y': y = absXyz ? v : y + v; break;
                case 'Z': z = absXyz ? v : z + v; break;
                case 'E': e = absE ? v : e + v; break;
            }
        }
    }

    static void Emit(
        Raw raw, Vector3 from, Vector3 to, bool extrude, bool layerChange, bool zHop, bool brim, int layer,
        ref Vector3? last)
    {
        if (last is { } prev && (from - prev).LengthSquared() > 0.01f)
        {
            raw.Moves.Add(new RawMove(prev, from, false, layerChange, false, false, layer));
            layerChange = false;
        }
        raw.Moves.Add(new RawMove(from, to, extrude, layerChange && !extrude, zHop, brim && extrude, layer));
        last = to;
    }

    static void EmitArc(
        Raw raw, Vector3 from, Vector3 to, float iOff, float jOff, float radius, bool clockwise, bool extrude,
        bool layerChange, bool zHop, bool brim, int layer, ref Vector3? last)
    {
        float cx, cy, r;
        if (!float.IsNaN(radius) && MathF.Abs(radius) > 1e-4f)
        {
            if (!TryCenterFromRadius(from, to, radius, clockwise, out cx, out cy, out r))
            {
                Emit(raw, from, to, extrude, layerChange, zHop, brim, layer, ref last);
                return;
            }
        }
        else
        {
            cx = from.X + iOff;
            cy = from.Y + jOff;
            r = MathF.Sqrt((from.X - cx) * (from.X - cx) + (from.Y - cy) * (from.Y - cy));
        }
        if (r < 1e-3f)
        {
            Emit(raw, from, to, extrude, layerChange, zHop, brim, layer, ref last);
            return;
        }

        float a0 = MathF.Atan2(from.Y - cy, from.X - cx);
        float a1 = MathF.Atan2(to.Y - cy, to.X - cx);
        float sweep = a1 - a0;
        if (clockwise)
        {
            if (sweep >= -1e-6f) sweep -= MathF.PI * 2f;
        }
        else if (sweep <= 1e-6f)
            sweep += MathF.PI * 2f;

        float chord = 0.25f;
        float step = 2f * MathF.Asin(Math.Clamp(chord / (2f * r), 0f, 1f));
        if (step < 1e-4f) step = 0.2f;
        int n = (int)MathF.Ceiling(MathF.Abs(sweep) / step);
        n = Math.Clamp(n, 1, 96);

        var prev = from;
        bool first = true;
        for (int k = 1; k <= n; k++)
        {
            float t = k / (float)n;
            float ang = a0 + sweep * t;
            var pt = new Vector3(
                cx + r * MathF.Cos(ang),
                cy + r * MathF.Sin(ang),
                from.Z + (to.Z - from.Z) * t);
            if (k == n)
                pt = new Vector3(to.X, to.Y, to.Z);
            Emit(raw, prev, pt, extrude, first && layerChange, first && zHop, brim, layer, ref last);
            first = false;
            prev = pt;
        }
    }

    static bool TryCenterFromRadius(Vector3 from, Vector3 to, float radius, bool clockwise, out float cx, out float cy, out float r)
    {
        cx = cy = 0;
        r = MathF.Abs(radius);
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d < 1e-6f || d > 2f * r + 1e-3f)
            return false;
        float h = MathF.Sqrt(Math.Max(0f, r * r - (d * 0.5f) * (d * 0.5f)));
        float mx = (from.X + to.X) * 0.5f;
        float my = (from.Y + to.Y) * 0.5f;
        float ox = -dy / d * h;
        float oy = dx / d * h;
        bool left = clockwise == radius < 0;
        cx = mx + (left ? ox : -ox);
        cy = my + (left ? oy : -oy);
        return true;
    }
}
