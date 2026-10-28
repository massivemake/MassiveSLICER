using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>Polylines read from an OBJ, ASCII DXF, or SVG file. Units are millimetres, Z up.</summary>
public sealed class CurveFileLoad
{
    public List<MillCurve> Curves { get; } = [];
    public string UnitNote { get; set; } = "";
    public bool Flat { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>
/// Reads toolpath curves. OBJ is the line-element file Export Polyline writes.
/// DXF is ASCII (line, polyline, arc, circle, spline). SVG paths are tessellated
/// and flipped from Y-down into the slicer frame.
/// </summary>
public static class CurveFileReader
{
    public const float ChordMm = 0.2f;
    const float WeldMm = 0.05f;

    public static CurveFileLoad Read(string path)
    {
        var load = new CurveFileLoad();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            load.Error = "Curve file was not found.";
            return load;
        }
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            switch (ext)
            {
                case ".obj":
                    ReadObj(File.ReadLines(path), name, load);
                    load.UnitNote = "OBJ millimetres.";
                    break;
                case ".dxf":
                    ReadDxf(path, name, load);
                    break;
                case ".svg":
                    ReadSvg(File.ReadAllText(path), name, load);
                    break;
                default:
                    load.Error = "Use an OBJ polyline, an ASCII DXF, or an SVG.";
                    return load;
            }
        }
        catch (Exception ex)
        {
            load.Error = "Could not read that curve file. " + ex.Message;
            load.Curves.Clear();
            return load;
        }
        load.Curves.RemoveAll(c => c.Points.Count < 2);
        load.Flat = load.Curves.Count > 0 && load.Curves.TrueForAll(IsFlat);
        if (load.Curves.Count == 0 && load.Error.Length == 0)
            load.Error = "That file has no curves.";
        return load;
    }

    public static void LiftFlat(IReadOnlyList<MillCurve> curves, float modelTopZ)
    {
        foreach (var curve in curves)
        {
            if (!IsFlat(curve) || curve.Points.Count == 0) continue;
            float z = curve.Points[0].Z;
            float lift = modelTopZ - z;
            if (MathF.Abs(lift) < 1e-3f) continue;
            for (int i = 0; i < curve.Points.Count; i++)
            {
                var p = curve.Points[i];
                curve.Points[i] = new Vector3(p.X, p.Y, p.Z + lift);
            }
        }
    }

    static bool IsFlat(MillCurve curve)
    {
        if (curve.Points.Count == 0) return true;
        float min = curve.Points[0].Z, max = min;
        foreach (var p in curve.Points)
        {
            if (p.Z < min) min = p.Z;
            if (p.Z > max) max = p.Z;
        }
        return max - min < 0.05f;
    }

    // -- OBJ ---------------------------------------------------------------------

    static void ReadObj(IEnumerable<string> lines, string name, CurveFileLoad load)
    {
        var verts = new List<Vector3>();
        var chains = new List<List<Vector3>>();
        List<Vector3>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var tok = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length == 0) continue;
            if (tok[0] == "v" && tok.Length >= 4)
            {
                verts.Add(new Vector3(F(tok[1]), F(tok[2]), F(tok[3])));
                continue;
            }
            if (tok[0] != "l" || tok.Length < 3) continue;
            var run = new List<Vector3>();
            for (int i = 1; i < tok.Length; i++)
            {
                if (!int.TryParse(tok[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                    continue;
                int resolved = index > 0 ? index - 1 : verts.Count + index;
                if ((uint)resolved >= (uint)verts.Count) continue;
                run.Add(verts[resolved]);
            }
            for (int i = 1; i < run.Count; i++)
                current = AppendSegment(chains, current, run[i - 1], run[i]);
        }
        CloseAndName(chains, name, load);
    }

    static List<Vector3> AppendSegment(List<List<Vector3>> chains, List<Vector3>? current, Vector3 a, Vector3 b)
    {
        if (Vector3.DistanceSquared(a, b) < 1e-10f) return current ?? [];
        if (current is { Count: > 0 } && Vector3.Distance(current[^1], a) <= WeldMm)
        {
            current.Add(b);
            return current;
        }
        if (current is { Count: > 0 } && Vector3.Distance(current[^1], b) <= WeldMm)
        {
            current.Add(a);
            return current;
        }
        var fresh = new List<Vector3> { a, b };
        chains.Add(fresh);
        return fresh;
    }

    static void CloseAndName(List<List<Vector3>> chains, string name, CurveFileLoad load)
    {
        int n = 1;
        foreach (var chain in chains)
        {
            if (chain.Count < 2) continue;
            bool closed = Vector3.Distance(chain[0], chain[^1]) <= WeldMm;
            load.Curves.Add(new MillCurve
            {
                Name = chains.Count == 1 ? name : $"{name} {n}",
                Closed = closed,
                Included = true,
                Points = chain,
            });
            n++;
        }
    }

    // -- DXF ---------------------------------------------------------------------

    static void ReadDxf(string path, string name, CurveFileLoad load)
    {
        using var stream = File.OpenRead(path);
        var mark = new byte[22];
        int read = stream.Read(mark, 0, mark.Length);
        if (read >= 18 && System.Text.Encoding.ASCII.GetString(mark, 0, 18) == "AutoCAD Binary DXF")
        {
            load.Error = "That DXF is binary. Save it as ASCII DXF and import it again.";
            return;
        }
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var groups = new List<(int Code, string Value)>();
        while (true)
        {
            string? codeLine = reader.ReadLine();
            if (codeLine is null) break;
            string? valueLine = reader.ReadLine();
            if (valueLine is null) break;
            if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                continue;
            groups.Add((code, valueLine.Trim()));
        }

        float scale = 1f;
        string unitNote = "DXF millimetres.";
        bool inHeader = false;
        bool wantUnits = false;
        var chains = new List<List<Vector3>>();
        DxfEntity? entity = null;

        void Flush()
        {
            if (entity is null) return;
            entity.Emit(chains, scale, ChordMm);
            entity = null;
        }

        foreach (var (code, value) in groups)
        {
            if (code == 0 && value == "SECTION") { inHeader = false; continue; }
            if (code == 2 && value == "HEADER") { inHeader = true; continue; }
            if (code == 0 && value == "ENDSEC")
            {
                Flush();
                inHeader = false;
                continue;
            }
            if (inHeader)
            {
                if (code == 9) wantUnits = value == "$INSUNITS";
                else if (wantUnits && code == 70)
                {
                    (scale, unitNote) = DxfScale(value);
                    wantUnits = false;
                }
                continue;
            }
            if (code == 0)
            {
                if (entity is DxfPoly poly && value is "VERTEX" or "SEQEND")
                {
                    poly.Take(0, value);
                    if (value == "SEQEND") Flush();
                    continue;
                }
                Flush();
                entity = value switch
                {
                    "LINE" => new DxfLine(),
                    "LWPOLYLINE" => new DxfPoly(light: true),
                    "POLYLINE" => new DxfPoly(light: false),
                    "ARC" => new DxfArc(circle: false),
                    "CIRCLE" => new DxfArc(circle: true),
                    "SPLINE" => new DxfSpline(),
                    _ => null,
                };
                continue;
            }
            entity?.Take(code, value);
        }
        Flush();
        load.UnitNote = unitNote;
        CloseAndName(chains, name, load);
    }

    static (float Scale, string Note) DxfScale(string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int unit))
            return (1f, "DXF units were not set, read as millimetres.");
        return unit switch
        {
            1 => (25.4f, "DXF inches converted to millimetres."),
            2 => (304.8f, "DXF feet converted to millimetres."),
            4 => (1f, "DXF millimetres."),
            5 => (10f, "DXF centimetres converted to millimetres."),
            6 => (1000f, "DXF metres converted to millimetres."),
            0 => (1f, "DXF had no units, read as millimetres."),
            _ => (1f, "DXF units were not recognised, read as millimetres."),
        };
    }

    abstract class DxfEntity
    {
        protected Vector3 Extrusion = Vector3.UnitZ;
        public abstract void Take(int code, string value);
        public abstract void Emit(List<List<Vector3>> chains, float scale, float chord);

        protected void SetExtrusion(int code, string value)
        {
            float v = F(value);
            if (code == 210) Extrusion.X = v;
            else if (code == 220) Extrusion.Y = v;
            else Extrusion.Z = v;
        }

        protected Vector3 World(float x, float y, float z, float scale)
        {
            var p = FromOcs(new Vector3(x, y, z), Extrusion);
            return p * scale;
        }
    }

    sealed class DxfLine : DxfEntity
    {
        float _x0, _y0, _z0, _x1, _y1, _z1;
        bool _hasEnd;
        public override void Take(int code, string value)
        {
            float v = F(value);
            switch (code)
            {
                case 10: _x0 = v; break;
                case 20: _y0 = v; break;
                case 30: _z0 = v; break;
                case 11: _x1 = v; _hasEnd = true; break;
                case 21: _y1 = v; break;
                case 31: _z1 = v; break;
                case 210 or 220 or 230: SetExtrusion(code, value); break;
            }
        }
        public override void Emit(List<List<Vector3>> chains, float scale, float chord)
        {
            if (!_hasEnd) return;
            chains.Add([World(_x0, _y0, _z0, scale), World(_x1, _y1, _z1, scale)]);
        }
    }

    sealed class DxfPoly : DxfEntity
    {
        readonly bool _light;
        readonly List<(float X, float Y, float Z, float Bulge)> _verts = [];
        float _x, _y, _z, _bulge, _elevation;
        bool _pending, _closed;
        public DxfPoly(bool light) => _light = light;
        public override void Take(int code, string value)
        {
            if (code == 0 && value == "VERTEX")
            {
                Push();
                return;
            }
            if (code == 0 && value is "VERTEX" or "SEQEND")
            {
                Push();
                return;
            }
            if (code == 70 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int flags))
                _closed = (flags & 1) != 0;
            if (code == 10)
            {
                if (_light) Push();
                _x = F(value);
                _pending = true;
                _bulge = 0;
            }
            else if (code == 20) _y = F(value);
            else if (code == 30) _z = F(value);
            else if (code == 38) _elevation = F(value);
            else if (code == 42) _bulge = F(value);
            else if (code is 210 or 220 or 230) SetExtrusion(code, value);
        }
        void Push()
        {
            if (!_pending) return;
            _verts.Add((_x, _y, _light ? _elevation : _z, _bulge));
            _pending = false;
            _bulge = 0;
        }
        public override void Emit(List<List<Vector3>> chains, float scale, float chord)
        {
            Push();
            if (_verts.Count < 2) return;
            var chain = new List<Vector3> { World(_verts[0].X, _verts[0].Y, _verts[0].Z, scale) };
            int last = _closed ? _verts.Count : _verts.Count - 1;
            for (int i = 0; i < last; i++)
            {
                var a = _verts[i];
                var b = _verts[(i + 1) % _verts.Count];
                foreach (var p in TessellateBulge(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), a.Bulge, chord / MathF.Max(scale, 1e-6f)))
                {
                    float z = a.Z + (b.Z - a.Z) * 0.5f;
                    chain.Add(World(p.X, p.Y, z, scale));
                }
                if (MathF.Abs(a.Bulge) < 1e-6f)
                    chain.Add(World(b.X, b.Y, b.Z, scale));
            }
            if (_closed && chain.Count >= 2 && Vector3.Distance(chain[0], chain[^1]) > WeldMm)
                chain.Add(chain[0]);
            chains.Add(chain);
        }
    }

    sealed class DxfArc : DxfEntity
    {
        readonly bool _circle;
        float _cx, _cy, _cz, _r, _start, _end = 360f;
        public DxfArc(bool circle) => _circle = circle;
        public override void Take(int code, string value)
        {
            float v = F(value);
            switch (code)
            {
                case 10: _cx = v; break;
                case 20: _cy = v; break;
                case 30: _cz = v; break;
                case 40: _r = v; break;
                case 50: _start = v; break;
                case 51: _end = v; break;
                case 210 or 220 or 230: SetExtrusion(code, value); break;
            }
        }
        public override void Emit(List<List<Vector3>> chains, float scale, float chord)
        {
            if (_r <= 1e-6f) return;
            float sweep = _circle ? 360f : _end - _start;
            if (!_circle && sweep <= 0f) sweep += 360f;
            float tol = chord / MathF.Max(scale, 1e-6f);
            int n = Math.Max(_circle ? 12 : 2, ArcSteps(_r, sweep * MathF.PI / 180f, tol));
            var chain = new List<Vector3>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                float deg = _start + sweep * i / n;
                float rad = deg * MathF.PI / 180f;
                chain.Add(World(_cx + _r * MathF.Cos(rad), _cy + _r * MathF.Sin(rad), _cz, scale));
            }
            chains.Add(chain);
        }
    }

    sealed class DxfSpline : DxfEntity
    {
        int _degree = 3;
        bool _closed;
        readonly List<float> _knots = [];
        readonly List<Vector3> _ctrl = [];
        readonly List<Vector3> _fit = [];
        float _x, _y, _z, _fx, _fy, _fz;
        bool _ctrlPending, _fitPending;
        public override void Take(int code, string value)
        {
            if (code == 71 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int deg))
                _degree = Math.Max(1, deg);
            else if (code == 70 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int flags))
                _closed = (flags & 1) != 0;
            else if (code == 40) _knots.Add(F(value));
            else if (code == 10) { PushCtrl(); _x = F(value); _ctrlPending = true; }
            else if (code == 20) _y = F(value);
            else if (code == 30) _z = F(value);
            else if (code == 11) { PushFit(); _fx = F(value); _fitPending = true; }
            else if (code == 21) _fy = F(value);
            else if (code == 31) _fz = F(value);
            else if (code is 210 or 220 or 230) SetExtrusion(code, value);
        }
        void PushCtrl()
        {
            if (!_ctrlPending) return;
            _ctrl.Add(new Vector3(_x, _y, _z));
            _ctrlPending = false;
        }
        void PushFit()
        {
            if (!_fitPending) return;
            _fit.Add(new Vector3(_fx, _fy, _fz));
            _fitPending = false;
        }
        public override void Emit(List<List<Vector3>> chains, float scale, float chord)
        {
            PushCtrl();
            PushFit();
            List<Vector3> local;
            if (_ctrl.Count >= 2 && _knots.Count >= _ctrl.Count + _degree + 1)
                local = SampleSpline(_degree, _knots, _ctrl, chord);
            else if (_fit.Count >= 2)
                local = _fit;
            else if (_ctrl.Count >= 2)
                local = _ctrl;
            else
                return;
            var chain = new List<Vector3>(local.Count);
            foreach (var p in local)
                chain.Add(World(p.X, p.Y, p.Z, scale));
            if (_closed && chain.Count >= 2 && Vector3.Distance(chain[0], chain[^1]) > WeldMm)
                chain.Add(chain[0]);
            chains.Add(chain);
        }
    }

    static List<Vector3> SampleSpline(int degree, List<float> knots, List<Vector3> ctrl, float chord)
    {
        int n = ctrl.Count - 1;
        int p = Math.Clamp(degree, 1, n);
        float t0 = knots[p];
        float t1 = knots[Math.Min(knots.Count - p - 1, knots.Count - 1)];
        if (t1 < t0) (t0, t1) = (t1, t0);
        var samples = new List<Vector3>();
        void Recurse(float a, float b, Vector3 pa, Vector3 pb, int depth)
        {
            float m = (a + b) * 0.5f;
            var pm = DeBoor(p, knots, ctrl, m);
            float bow = Vector3.Distance(pm, (pa + pb) * 0.5f);
            if (depth > 8 || (bow <= chord && Vector3.Distance(pa, pb) <= chord * 8f))
                samples.Add(pb);
            else
            {
                Recurse(a, m, pa, pm, depth + 1);
                Recurse(m, b, pm, pb, depth + 1);
            }
        }
        var start = DeBoor(p, knots, ctrl, t0);
        samples.Add(start);
        int spans = Math.Max(1, ctrl.Count * 2);
        var prevT = t0;
        var prevP = start;
        for (int i = 1; i <= spans; i++)
        {
            float t = t0 + (t1 - t0) * i / spans;
            var pnt = DeBoor(p, knots, ctrl, t);
            Recurse(prevT, t, prevP, pnt, 0);
            prevT = t;
            prevP = pnt;
        }
        return samples;
    }

    static Vector3 DeBoor(int degree, List<float> knots, List<Vector3> ctrl, float t)
    {
        int n = ctrl.Count - 1;
        int p = degree;
        if (t <= knots[p]) return ctrl[0];
        if (t >= knots[n + 1]) return ctrl[n];
        int k = p;
        for (int i = p; i <= n; i++)
        {
            if (t < knots[i + 1] || i == n) { k = i; break; }
        }
        var d = new Vector3[p + 1];
        for (int j = 0; j <= p; j++)
            d[j] = ctrl[k - p + j];
        for (int r = 1; r <= p; r++)
        {
            for (int j = p; j >= r; j--)
            {
                int idx = k - p + j;
                float left = knots[idx];
                float right = knots[idx + p - r + 1];
                float denom = right - left;
                float alpha = MathF.Abs(denom) < 1e-8f ? 0f : (t - left) / denom;
                d[j] = d[j - 1] * (1f - alpha) + d[j] * alpha;
            }
        }
        return d[p];
    }

    static Vector3 FromOcs(Vector3 p, Vector3 extrusion)
    {
        if (extrusion.LengthSquared() < 1e-8f) return p;
        var n = Vector3.Normalize(extrusion);
        if (n.Z > 0.9999f && MathF.Abs(n.X) < 1e-4f && MathF.Abs(n.Y) < 1e-4f) return p;
        var wy = MathF.Abs(n.X) < 1f / 64f && MathF.Abs(n.Y) < 1f / 64f ? Vector3.UnitY : Vector3.UnitZ;
        var ax = Vector3.Normalize(Vector3.Cross(wy, n));
        var ay = Vector3.Normalize(Vector3.Cross(n, ax));
        return ax * p.X + ay * p.Y + n * p.Z;
    }

    static List<Vector2> TessellateBulge(Vector2 p1, Vector2 p2, float bulge, float tol)
    {
        var pts = new List<Vector2>();
        if (MathF.Abs(bulge) < 1e-6f) return pts;
        float dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        float chord = MathF.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-8f) return pts;
        float r = chord * (bulge * bulge + 1f) / (4f * MathF.Abs(bulge));
        float mx = (p1.X + p2.X) * 0.5f, my = (p1.Y + p2.Y) * 0.5f;
        float lx = -dy / chord, ly = dx / chord;
        float cosHalf = (1f - bulge * bulge) / (1f + bulge * bulge);
        float sign = MathF.Sign(bulge);
        float cx = mx - lx * sign * r * cosHalf;
        float cy = my - ly * sign * r * cosHalf;
        float a1 = MathF.Atan2(p1.Y - cy, p1.X - cx);
        float a2 = MathF.Atan2(p2.Y - cy, p2.X - cx);
        float sweep = a2 - a1;
        if (bulge > 0f) { if (sweep < 0f) sweep += MathF.Tau; }
        else if (sweep > 0f) sweep -= MathF.Tau;
        int steps = Math.Max(1, ArcSteps(r, sweep, tol));
        for (int i = 1; i <= steps; i++)
        {
            float a = a1 + sweep * i / steps;
            pts.Add(new Vector2(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a)));
        }
        return pts;
    }

    static int ArcSteps(float radius, float sweepRad, float tol)
    {
        float r = MathF.Max(radius, 1e-4f);
        float step = 2f * MathF.Acos(Math.Clamp(1f - tol / r, -1f, 1f));
        if (step < 1e-3f) step = 1e-3f;
        return (int)MathF.Ceiling(MathF.Abs(sweepRad) / step);
    }

    // -- SVG ---------------------------------------------------------------------

    static void ReadSvg(string text, string name, CurveFileLoad load)
    {
        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var svg = doc.Root ?? throw new InvalidOperationException("SVG has no root.");
        var (scale, note) = SvgScale(svg);
        load.UnitNote = note;
        var chains = new List<List<Vector2>>();
        Walk(svg, Matrix3x2.Identity, chains, scale);
        int n = 1;
        foreach (var chain in chains)
        {
            if (chain.Count < 2) continue;
            bool closed = Vector2.Distance(chain[0], chain[^1]) <= WeldMm;
            var points = new List<Vector3>(chain.Count);
            foreach (var p in chain)
                points.Add(new Vector3(p.X, -p.Y, 0f));
            load.Curves.Add(new MillCurve
            {
                Name = chains.Count == 1 ? name : $"{name} {n}",
                Closed = closed,
                Included = true,
                Points = points,
            });
            n++;
        }
    }

    static (float Scale, string Note) SvgScale(XElement svg)
    {
        string width = Attr(svg, "width");
        string height = Attr(svg, "height");
        var box = Attr(svg, "viewBox").Split((char[])[' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        float viewW = box.Length == 4 && float.TryParse(box[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float w) ? w : 0f;
        if (TryLength(width, out float widthPx, out string unit) && viewW > 1e-6f)
        {
            string note = unit is "px" or ""
                ? "SVG pixels read as millimetres."
                : $"SVG {unit} converted to millimetres.";
            return (widthPx / viewW, note);
        }
        if (viewW <= 0f && TryLength(width, out _, out string u))
        {
            string note = u is "mm" or "cm" or "in"
                ? "SVG coordinates read as millimetres."
                : "SVG pixels read as millimetres.";
            return (1f, note);
        }
        _ = height;
        return (1f, "SVG pixels read as millimetres.");
    }

    static bool TryLength(string raw, out float px, out string unit)
    {
        px = 0f;
        unit = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        int i = 0;
        while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] is '.' or '-' or '+' or 'e' or 'E')) i++;
        if (i == 0 || !float.TryParse(raw[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            return false;
        unit = raw[i..].Trim().ToLowerInvariant();
        float toMm = unit switch
        {
            "mm" => 1f,
            "cm" => 10f,
            "in" => 25.4f,
            "pt" => 25.4f / 72f,
            "pc" => 25.4f / 6f,
            "px" or "" => 1f,
            _ => 1f,
        };
        px = value * toMm;
        return true;
    }

    static void Walk(XElement el, Matrix3x2 parent, List<List<Vector2>> chains, float scale)
    {
        if (Attr(el, "display").Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        var local = ParseTransform(Attr(el, "transform")) * parent;
        string tag = el.Name.LocalName.ToLowerInvariant();
        switch (tag)
        {
            case "line":
                chains.Add(ScaleChain([
                    new Vector2(Num(el, "x1"), Num(el, "y1")),
                    new Vector2(Num(el, "x2"), Num(el, "y2")),
                ], local, scale));
                break;
            case "polyline":
            case "polygon":
                var poly = ParsePairs(Attr(el, "points"));
                if (tag == "polygon" && poly.Count >= 3 && Vector2.Distance(poly[0], poly[^1]) > 1e-4f)
                    poly.Add(poly[0]);
                if (poly.Count >= 2) chains.Add(ScaleChain(poly, local, scale));
                break;
            case "rect":
                float x = Num(el, "x"), y = Num(el, "y"), w = Num(el, "width"), h = Num(el, "height");
                if (w > 0f && h > 0f)
                    chains.Add(ScaleChain([
                        new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h), new(x, y),
                    ], local, scale));
                break;
            case "circle":
            case "ellipse":
                float rx = tag == "circle" ? Num(el, "r") : Num(el, "rx");
                float ry = tag == "circle" ? rx : Num(el, "ry");
                if (rx > 0f && ry > 0f)
                    chains.Add(ScaleChain(Ellipse(Num(el, "cx"), Num(el, "cy"), rx, ry, scale), local, scale));
                break;
            case "path":
                foreach (var sub in ParsePath(Attr(el, "d"), ChordMm / MathF.Max(scale, 1e-6f)))
                    chains.Add(ScaleChain(sub, local, scale));
                break;
        }
        foreach (var child in el.Elements())
            Walk(child, local, chains, scale);
    }

    static List<Vector2> ScaleChain(List<Vector2> local, Matrix3x2 matrix, float scale)
    {
        var world = new List<Vector2>(local.Count);
        foreach (var p in local)
        {
            var t = Vector2.Transform(p, matrix);
            world.Add(t * scale);
        }
        return world;
    }

    static List<Vector2> Ellipse(float cx, float cy, float rx, float ry, float scale)
    {
        float tol = ChordMm / MathF.Max(scale, 1e-6f);
        int n = Math.Max(12, ArcSteps(MathF.Max(rx, ry), MathF.Tau, tol));
        var pts = new List<Vector2>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            float a = MathF.Tau * i / n;
            pts.Add(new Vector2(cx + rx * MathF.Cos(a), cy + ry * MathF.Sin(a)));
        }
        return pts;
    }

    static Matrix3x2 ParseTransform(string raw)
    {
        var m = Matrix3x2.Identity;
        if (string.IsNullOrWhiteSpace(raw)) return m;
        int i = 0;
        while (i < raw.Length)
        {
            while (i < raw.Length && !char.IsLetter(raw[i])) i++;
            if (i >= raw.Length) break;
            int start = i;
            while (i < raw.Length && char.IsLetter(raw[i])) i++;
            string op = raw[start..i];
            while (i < raw.Length && raw[i] != '(') i++;
            if (i >= raw.Length) break;
            i++;
            int end = raw.IndexOf(')', i);
            if (end < 0) break;
            var nums = ReadNumbers(raw[i..end]);
            i = end + 1;
            var next = op switch
            {
                "matrix" when nums.Count >= 6 => new Matrix3x2(nums[0], nums[1], nums[2], nums[3], nums[4], nums[5]),
                "translate" when nums.Count >= 1 => Matrix3x2.CreateTranslation(nums[0], nums.Count > 1 ? nums[1] : 0f),
                "scale" when nums.Count >= 1 => Matrix3x2.CreateScale(nums[0], nums.Count > 1 ? nums[1] : nums[0]),
                "rotate" when nums.Count >= 3 => RotateAbout(nums[0], nums[1], nums[2]),
                "rotate" when nums.Count >= 1 => Matrix3x2.CreateRotation(nums[0] * MathF.PI / 180f),
                _ => Matrix3x2.Identity,
            };
            m = next * m;
        }
        return m;
    }

    static Matrix3x2 RotateAbout(float deg, float cx, float cy)
    {
        var t = Matrix3x2.CreateTranslation(cx, cy);
        var r = Matrix3x2.CreateRotation(deg * MathF.PI / 180f);
        var back = Matrix3x2.CreateTranslation(-cx, -cy);
        return back * r * t;
    }

    static List<List<Vector2>> ParsePath(string d, float tol)
    {
        var subs = new List<List<Vector2>>();
        List<Vector2>? cur = null;
        var start = Vector2.Zero;
        var pen = Vector2.Zero;
        Vector2? prevCubic = null;
        Vector2? prevQuad = null;
        char cmd = 'L';
        int i = 0;

        bool Read(out float v) => TryNum(d, ref i, out v);
        bool ReadPoint(bool rel, out Vector2 p)
        {
            p = default;
            if (!Read(out float x) || !Read(out float y)) return false;
            p = rel ? pen + new Vector2(x, y) : new Vector2(x, y);
            return true;
        }

        void LineTo(Vector2 p)
        {
            cur ??= [];
            if (cur.Count == 0) cur.Add(pen);
            cur.Add(p);
            pen = p;
            prevCubic = null;
            prevQuad = null;
        }

        while (i < d.Length)
        {
            Skip(d, ref i);
            if (i >= d.Length) break;
            if (char.IsLetter(d[i]))
                cmd = d[i++];
            bool rel = char.IsLower(cmd);
            char op = char.ToUpperInvariant(cmd);
            if (op == 'Z')
            {
                if (cur is { Count: >= 2 })
                {
                    if (Vector2.Distance(cur[^1], start) > 1e-4f) cur.Add(start);
                    subs.Add(cur);
                }
                cur = null;
                pen = start;
                prevCubic = null;
                prevQuad = null;
                continue;
            }
            Skip(d, ref i);
            if (i >= d.Length) break;
            if (char.IsLetter(d[i])) continue;
            if (op == 'M')
            {
                if (cur is { Count: >= 2 }) subs.Add(cur);
                if (!ReadPoint(rel, out var p)) break;
                cur = [p];
                start = pen = p;
                prevCubic = null;
                prevQuad = null;
                cmd = rel ? 'l' : 'L';
                continue;
            }
            if (op == 'L')
            {
                if (!ReadPoint(rel, out var p)) break;
                LineTo(p);
                continue;
            }
            if (op == 'H')
            {
                if (!Read(out float x)) break;
                LineTo(new Vector2(rel ? pen.X + x : x, pen.Y));
                continue;
            }
            if (op == 'V')
            {
                if (!Read(out float y)) break;
                LineTo(new Vector2(pen.X, rel ? pen.Y + y : y));
                continue;
            }
            if (op == 'C')
            {
                if (!ReadPoint(rel, out var c1) || !ReadPoint(rel, out var c2) || !ReadPoint(rel, out var p)) break;
                cur ??= [pen];
                FlattenCubic(cur, pen, c1, c2, p, tol);
                prevCubic = c2;
                prevQuad = null;
                pen = p;
                continue;
            }
            if (op == 'S')
            {
                var c1 = prevCubic is { } pc ? pen * 2f - pc : pen;
                if (!ReadPoint(rel, out var c2) || !ReadPoint(rel, out var p)) break;
                cur ??= [pen];
                FlattenCubic(cur, pen, c1, c2, p, tol);
                prevCubic = c2;
                prevQuad = null;
                pen = p;
                continue;
            }
            if (op == 'Q')
            {
                if (!ReadPoint(rel, out var c) || !ReadPoint(rel, out var p)) break;
                cur ??= [pen];
                FlattenQuad(cur, pen, c, p, tol);
                prevQuad = c;
                prevCubic = null;
                pen = p;
                continue;
            }
            if (op == 'T')
            {
                var c = prevQuad is { } pq ? pen * 2f - pq : pen;
                if (!ReadPoint(rel, out var p)) break;
                cur ??= [pen];
                FlattenQuad(cur, pen, c, p, tol);
                prevQuad = c;
                prevCubic = null;
                pen = p;
                continue;
            }
            if (op == 'A')
            {
                if (!Read(out float rx) || !Read(out float ry) || !Read(out float rot)
                    || !Read(out float largeN) || !Read(out float sweepN) || !ReadPoint(rel, out var p))
                    break;
                cur ??= [pen];
                FlattenSvgArc(cur, pen, MathF.Abs(rx), MathF.Abs(ry), rot, largeN != 0f, sweepN != 0f, p, tol);
                pen = p;
                prevCubic = null;
                prevQuad = null;
                continue;
            }
            break;
        }
        if (cur is { Count: >= 2 }) subs.Add(cur);
        return subs;
    }

    static void FlattenCubic(List<Vector2> dest, Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1, float tol)
    {
        void Recurse(Vector2 a, Vector2 b, Vector2 c, Vector2 d, int depth)
        {
            float chord = Vector2.Distance(a, d);
            float bow = MathF.Max(DistanceToSegment(b, a, d), DistanceToSegment(c, a, d));
            if (depth > 8 || bow <= tol || chord < 1e-4f)
            {
                dest.Add(d);
                return;
            }
            var ab = (a + b) * 0.5f;
            var bc = (b + c) * 0.5f;
            var cd = (c + d) * 0.5f;
            var abc = (ab + bc) * 0.5f;
            var bcd = (bc + cd) * 0.5f;
            var mid = (abc + bcd) * 0.5f;
            Recurse(a, ab, abc, mid, depth + 1);
            Recurse(mid, bcd, cd, d, depth + 1);
        }
        Recurse(p0, c1, c2, p1, 0);
    }

    static void FlattenQuad(List<Vector2> dest, Vector2 p0, Vector2 c, Vector2 p1, float tol)
    {
        void Recurse(Vector2 a, Vector2 b, Vector2 d, int depth)
        {
            if (depth > 8 || DistanceToSegment(b, a, d) <= tol)
            {
                dest.Add(d);
                return;
            }
            var ab = (a + b) * 0.5f;
            var bd = (b + d) * 0.5f;
            var mid = (ab + bd) * 0.5f;
            Recurse(a, ab, mid, depth + 1);
            Recurse(mid, bd, d, depth + 1);
        }
        Recurse(p0, c, p1, 0);
    }

    static void FlattenSvgArc(
        List<Vector2> dest, Vector2 p0, float rx, float ry, float deg, bool large, bool sweep, Vector2 p1, float tol)
    {
        if (rx < 1e-6f || ry < 1e-6f || Vector2.DistanceSquared(p0, p1) < 1e-10f)
        {
            dest.Add(p1);
            return;
        }
        float phi = deg * MathF.PI / 180f;
        float cos = MathF.Cos(phi), sin = MathF.Sin(phi);
        float dx = (p0.X - p1.X) * 0.5f, dy = (p0.Y - p1.Y) * 0.5f;
        float x1 = cos * dx + sin * dy;
        float y1 = -sin * dx + cos * dy;
        float rx2 = rx * rx, ry2 = ry * ry;
        float x12 = x1 * x1, y12 = y1 * y1;
        float lam = x12 / rx2 + y12 / ry2;
        if (lam > 1f)
        {
            float s = MathF.Sqrt(lam);
            rx *= s; ry *= s; rx2 = rx * rx; ry2 = ry * ry;
        }
        float num = MathF.Max(0f, rx2 * ry2 - rx2 * y12 - ry2 * x12);
        float den = rx2 * y12 + ry2 * x12;
        float coef = MathF.Sqrt(den < 1e-8f ? 0f : num / den);
        if (large == sweep) coef = -coef;
        float cxp = coef * rx * y1 / ry;
        float cyp = coef * -ry * x1 / rx;
        float cx = cos * cxp - sin * cyp + (p0.X + p1.X) * 0.5f;
        float cy = sin * cxp + cos * cyp + (p0.Y + p1.Y) * 0.5f;
        float theta1 = SvgAngle(1f, 0f, (x1 - cxp) / rx, (y1 - cyp) / ry);
        float delta = SvgAngle((x1 - cxp) / rx, (y1 - cyp) / ry, (-x1 - cxp) / rx, (-y1 - cyp) / ry);
        if (!sweep && delta > 0f) delta -= MathF.Tau;
        if (sweep && delta < 0f) delta += MathF.Tau;
        int steps = Math.Max(2, ArcSteps(MathF.Max(rx, ry), delta, tol));
        for (int s = 1; s <= steps; s++)
        {
            float th = theta1 + delta * s / steps;
            float cosTh = MathF.Cos(th), sinTh = MathF.Sin(th);
            float x = cos * rx * cosTh - sin * ry * sinTh + cx;
            float y = sin * rx * cosTh + cos * ry * sinTh + cy;
            dest.Add(new Vector2(x, y));
        }
    }

    static float SvgAngle(float ux, float uy, float vx, float vy)
    {
        float dot = Math.Clamp(ux * vx + uy * vy, -1f, 1f);
        float sign = ux * vy - uy * vx < 0f ? -1f : 1f;
        return sign * MathF.Acos(dot);
    }

    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-12f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    static List<float> ReadNumbers(string raw)
    {
        var list = new List<float>();
        int i = 0;
        while (TryNum(raw, ref i, out float v)) list.Add(v);
        return list;
    }

    static void Skip(string s, ref int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
    }

    static bool TryNum(string s, ref int i, out float v)
    {
        v = 0f;
        Skip(s, ref i);
        if (i >= s.Length) return false;
        int start = i;
        if (s[i] is '+' or '-') i++;
        bool any = false;
        while (i < s.Length && char.IsDigit(s[i])) { any = true; i++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsDigit(s[i])) { any = true; i++; }
        }
        if (!any) { i = start; return false; }
        if (i < s.Length && s[i] is 'e' or 'E')
        {
            int e = i;
            i++;
            if (i < s.Length && s[i] is '+' or '-') i++;
            int exp = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == exp) i = e;
        }
        return float.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    static List<Vector2> ParsePairs(string raw)
    {
        var nums = ReadNumbers(raw);
        var pts = new List<Vector2>(nums.Count / 2);
        for (int i = 0; i + 1 < nums.Count; i += 2)
            pts.Add(new Vector2(nums[i], nums[i + 1]));
        return pts;
    }

    static string Attr(XElement el, string name)
        => el.Attribute(name)?.Value ?? el.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";

    static float Num(XElement el, string name)
        => float.TryParse(Attr(el, name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

    static float F(string raw)
        => float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
}
