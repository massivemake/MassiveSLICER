using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>How the pattern coordinate wraps around the part.</summary>
public enum PatternMappingMode
{
    /// <summary>Distance along the printed contour — cycles are evenly spaced in mm
    /// along the whole path regardless of shape.</summary>
    ArcLength,
    /// <summary>Polar angle around the part centre (classic OGcode cylindrical wrap) —
    /// stretches where the wall is far from centre, compresses where it is close.</summary>
    Radial,
    /// <summary>Fixed physical wavelength in mm along the path, phase-anchored at the
    /// seam — identical cycle size on every layer; the fractional remainder lands at
    /// the seam line instead of smearing into diagonal bands.</summary>
    Wavelength,
}

/// <summary>Decorative wall patterns (ported from the MassiveCODE effector project).</summary>
public enum PatternType
{
    Smooth, Sine, Diamond, Polygon, Ripple, HWave, VWave, Bumps,
    Bubbles, Pleats, Voronoi, Hexagon, Triangle, Guilloche, Hammered, Sunflower
}

/// <summary>
/// Toolpath post-processor that displaces contour walls with a decorative pattern —
/// a direct port of the MassiveCODE (OGcode) pattern engine. Each point is displaced
/// along the horizontal contour normal by <c>amplitude · fade(z) · P(θ + twist·z − offset, z)</c>,
/// where θ is the polar angle around the part centre and P is one of 16 pattern functions.
/// Runs after <see cref="WaveEffect"/> in the slicing pipeline.
/// </summary>
public static class PatternEffect
{
    private const float TwoPi = 2f * MathF.PI;

    public static Toolpath Apply(Toolpath toolpath, SliceSettings settings)
    {
        bool effectorActive = settings.EffectorPoints.Count > 0
            && (settings.EffectorMode == EffectorMode.Erase || settings.EffectorStrengthMm > 0f);
        if (settings.PatternType == PatternType.Smooth ||
            (settings.PatternAmplitude <= 0f && !effectorActive))
            return toolpath;
        if (toolpath.Layers.Count == 0) return toolpath;

        // -- Model frame: XY centre, z range, mean radius --------------------
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var layer in toolpath.Layers)
            foreach (var m in layer.Moves)
            {
                if (m.Kind != MoveKind.Extrude) continue;
                minX = MathF.Min(minX, m.To.X); maxX = MathF.Max(maxX, m.To.X);
                minY = MathF.Min(minY, m.To.Y); maxY = MathF.Max(maxY, m.To.Y);
                minZ = MathF.Min(minZ, m.To.Z); maxZ = MathF.Max(maxZ, m.To.Z);
            }
        if (minX > maxX) return toolpath;

        var ctx = new PatternContext
        {
            Type      = settings.PatternType,
            Amplitude = settings.PatternAmplitude,
            Frequency = MathF.Max(0.5f, settings.PatternFrequency),
            TwistRad  = settings.PatternTwistDegPerMm * MathF.PI / 180f,
            OffsetRad = settings.PatternOffsetDeg * MathF.PI / 180f,
            FadeIn    = MathF.Max(0f, settings.PatternFadeInMm),
            FadeOut   = MathF.Max(0f, settings.PatternFadeOutMm),
            Effectors = settings.EffectorPoints,
            EffectorRadius   = MathF.Max(1e-3f, settings.EffectorRadiusMm),
            EffectorStrength = MathF.Max(0f, settings.EffectorStrengthMm),
            EffectorMode     = settings.EffectorMode,
            Cx        = (minX + maxX) * 0.5f,
            Cy        = (minY + maxY) * 0.5f,
            ZMin      = minZ,
            Height    = MathF.Max(1f, maxZ - minZ),
            Radius    = MathF.Max(1f, ((maxX - minX) + (maxY - minY)) * 0.25f),
        };
        // Cell size (mm) used by the tiled patterns — one cell per frequency division.
        ctx.CellMm = MathF.Max(2f, TwoPi * ctx.Radius / ctx.Frequency);
        if (ctx.Type == PatternType.Sunflower) ctx.BuildSunflower();

        bool arcMode = settings.PatternMapping != PatternMappingMode.Radial;
        bool wavelengthMode = settings.PatternMapping == PatternMappingMode.Wavelength;
        float wavelength = MathF.Max(2f, settings.PatternWavelengthMm);
        if (wavelengthMode)
            ctx.CellMm = wavelength;   // square texture cells: z cell = path cell

        var result = new Toolpath();
        foreach (var layer in toolpath.Layers)
        {
            var newLayer = new ToolpathLayer(layer.Index, layer.Z) { PlaneNormal = layer.PlaneNormal };

            // Arc-length mode: pre-walk the layer's contour chains so each sample knows
            // its distance along the loop. u = 2π·(distance − anchor)/total gives evenly
            // spaced cycles; the anchor (vertex nearest world +X from the part centre)
            // keeps the phase aligned layer over layer even as seams wander.
            ChainInfo[]? chainOf = arcMode ? BuildChains(layer, ctx) : null;

            // Skin-only: displace the wall, then carry the structure's ENDS along with it so
            // braces stay attached without being bowed. The wall is walked first to learn where
            // it moved, so this pass has to run before the emit loop below.
            var wallField = new SkinOnlyBracing.WallField();
            (Vector3 AtFrom, Vector3 AtTo)[]? structureBlend = null;
            if (settings.PatternSkinOnly)
            {
                for (int mi = 0; mi < layer.Moves.Count; mi++)
                {
                    var m = layer.Moves[mi];
                    if (m.Kind != MoveKind.Extrude || m.IsLayerStitch || !m.IsWall) continue;
                    if (Vector3.Distance(m.From, m.To) < 1e-4f) continue;
                    var tan = Vector3.Normalize(m.To - m.From);
                    var pp  = Vector3.Cross(tan, Vector3.UnitZ);
                    if (pp.LengthSquared() < 1e-9f) continue;
                    pp = Vector3.Normalize(pp);
                    var chainW = chainOf?[mi];
                    float? thetaW = ChainTheta(chainW, ctx, wavelengthMode, wavelength,
                                               Vector3.Distance(m.From, m.To), 0f);
                    wallField.Record(m.From, m.From + pp * ctx.Displacement(m.From, thetaW));
                }
                structureBlend = SkinOnlyBracing.BlendForStructure(layer.Moves, wallField);
            }

            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                if (move.Kind != MoveKind.Extrude || move.IsLayerStitch)
                {
                    newLayer.Moves.Add(move);
                    continue;
                }

                // Structure under skin-only: one straight segment, ends riding the wall.
                if (structureBlend is not null && SkinOnlyBracing.IsStructure(move))
                {
                    var (dFrom, dTo) = structureBlend[mi];
                    newLayer.Moves.Add(move with { From = move.From + dFrom, To = move.To + dTo });
                    continue;
                }

                float len = Vector3.Distance(move.From, move.To);
                if (len < 1e-4f) { newLayer.Moves.Add(move); continue; }

                var chain = chainOf?[mi];

                // Sample finely enough for the pattern's detail along the path.
                float pathPerCycle = wavelengthMode
                    ? wavelength
                    : arcMode && chain is { Total: > 1f }
                        ? chain.Total / ctx.Frequency
                        : TwoPi * ctx.Radius / ctx.Frequency;
                float spacing  = Math.Clamp(pathPerCycle / 12f, 1.0f, 6f);
                int   segments = Math.Clamp((int)MathF.Ceiling(len / spacing), 1, 2000);

                var tangent = Vector3.Normalize(move.To - move.From);
                var perp    = Vector3.Cross(tangent, Vector3.UnitZ);
                if (perp.LengthSquared() < 1e-9f) { newLayer.Moves.Add(move); continue; }
                perp = Vector3.Normalize(perp);

                Vector3 Displaced(float t)
                {
                    var pt = Vector3.Lerp(move.From, move.To, t);
                    return pt + perp * ctx.Displacement(
                        pt, ChainTheta(chain, ctx, wavelengthMode, wavelength, len, t));
                }

                for (int seg = 0; seg < segments; seg++)
                {
                    newLayer.Moves.Add(move with
                    {
                        From = Displaced(seg / (float)segments),
                        To   = Displaced((seg + 1) / (float)segments),
                    });
                }
            }
            result.Layers.Add(newLayer);
        }
        return result;
    }

    /// <summary>
    /// Pattern phase at parameter <paramref name="t"/> along a move, or null outside arc modes.
    /// Shared so the wall sampling pass and the emit pass evaluate identical phase.
    /// </summary>
    private static float? ChainTheta(ChainInfo? chain, PatternContext ctx,
        bool wavelengthMode, float wavelength, float len, float t)
    {
        if (chain is not { Total: > 1f }) return null;

        if (wavelengthMode)
        {
            // Constant mm wavelength, phase 0 at the chain start (the seam): theta advances
            // 2π per Frequency·λ of path, so P(θ·f) completes one cycle every λ mm.
            float d = chain.CumStart + t * len;
            return TwoPi * d / (ctx.Frequency * wavelength);
        }

        float dist = chain.CumStart + t * len - chain.Anchor;
        dist -= MathF.Floor(dist / chain.Total) * chain.Total;
        return TwoPi * dist / chain.Total;
    }

    /// <summary>Per-move chain data for arc-length mapping.</summary>
    private sealed class ChainInfo
    {
        public float CumStart;   // path distance at this move's From
        public float Total;      // full chain length
        public float Anchor;     // path distance of the vertex nearest world +X
    }

    /// <summary>
    /// Segments a layer's moves into contiguous extrude chains and computes, per move,
    /// its cumulative start distance, the chain total, and the phase anchor.
    /// </summary>
    private static ChainInfo[] BuildChains(ToolpathLayer layer, PatternContext ctx)
    {
        var infos = new ChainInfo[layer.Moves.Count];
        int i = 0;
        while (i < layer.Moves.Count)
        {
            var m = layer.Moves[i];
            if (m.Kind != MoveKind.Extrude || m.IsLayerStitch) { i++; continue; }

            // Collect the contiguous chain starting here.
            int start = i;
            var cum = new List<float> { 0f };
            float total = 0f;
            float bestAngle = float.MaxValue, anchor = 0f;

            void ConsiderAnchor(Vector3 p, float distAlong)
            {
                float ang = MathF.Abs(MathF.Atan2(p.Y - ctx.Cy, p.X - ctx.Cx));
                if (ang < bestAngle) { bestAngle = ang; anchor = distAlong; }
            }

            ConsiderAnchor(m.From, 0f);
            int j = i;
            var prevTo = m.From;
            while (j < layer.Moves.Count)
            {
                var mv = layer.Moves[j];
                if (mv.Kind != MoveKind.Extrude || mv.IsLayerStitch) break;
                if (Vector3.DistanceSquared(mv.From, prevTo) > 1.0f) break;   // path jump ends the chain
                total += Vector3.Distance(mv.From, mv.To);
                cum.Add(total);
                ConsiderAnchor(mv.To, total);
                prevTo = mv.To;
                j++;
            }

            for (int k = start; k < j; k++)
                infos[k] = new ChainInfo { CumStart = cum[k - start], Total = total, Anchor = anchor };
            i = Math.Max(j, i + 1);
        }
        return infos;
    }

    // ── Pattern evaluation ──────────────────────────────────────────────────

    private sealed class PatternContext
    {
        public PatternType Type;
        public float Amplitude, Frequency, TwistRad, OffsetRad, FadeIn, FadeOut;
        public IReadOnlyList<Vector3> Effectors = [];
        public float EffectorRadius = 400f, EffectorStrength;
        public EffectorMode EffectorMode = EffectorMode.Amplify;
        public float Cx, Cy, ZMin, Height, Radius, CellMm;
        private (float theta, float z)[] _sunflower = [];
        private float _sunBumpR;

        public float Displacement(Vector3 p, float? loopTheta = null)
        {
            float z = p.Z - ZMin;
            float theta = loopTheta ?? MathF.Atan2(p.Y - Cy, p.X - Cx);
            float fade = 1f;
            if (FadeIn  > 0f) fade *= Math.Clamp(z / FadeIn, 0f, 1f);
            if (FadeOut > 0f) fade *= Math.Clamp((Height - z) / FadeOut, 0f, 1f);
            if (fade <= 0f) return 0f;
            // Live effector. Amplify: smoothstep bell boosts the local amplitude
            // (OGcode model). Erase: the pattern is simply not applied inside the
            // influence area — the amplitude is zeroed before displacement through
            // the inner region and blends back over the outer edge of the radius.
            float amp = EffectorMode == EffectorMode.Erase
                ? Amplitude * (1f - EffectorErase(p))
                : Amplitude + EffectorStrength * EffectorBell(p);
            if (amp <= 0f) return 0f;
            return amp * fade * Value(theta + TwistRad * z - OffsetRad, z);
        }

        /// <summary>Max smoothstep falloff t²(3−2t) over all effector points; 0 outside radius.</summary>
        /// <summary>
        /// Erase suppression in [0,1]: 1 (pattern fully off) anywhere within the inner
        /// 60% of the influence radius, smoothstep-blending to 0 across the outer band.
        /// The bell curve used by Amplify only hits 1 exactly AT the point — an effector
        /// hovering off the wall would never fully erase; this profile guarantees a
        /// clean flat core with a seamless transition at the boundary.
        /// </summary>
        private float EffectorErase(Vector3 p)
        {
            if (Effectors.Count == 0) return 0f;
            float best = 0f;
            foreach (var e in Effectors)
            {
                float dist = Vector3.Distance(p, e);
                if (dist >= EffectorRadius) continue;
                float prox = 1f - dist / EffectorRadius;    // 1 at the point → 0 at the edge
                float t = Math.Clamp(prox / 0.4f, 0f, 1f);  // saturates at 60% of the radius
                float s = t * t * (3f - 2f * t);
                if (s > best) best = s;
            }
            return best;
        }

        private float EffectorBell(Vector3 p)
        {
            if (Effectors.Count == 0) return 0f;
            if (EffectorStrength <= 0f) return 0f;
            float best = 0f;
            foreach (var e in Effectors)
            {
                float dist = Vector3.Distance(p, e);
                if (dist >= EffectorRadius) continue;
                float t = 1f - dist / EffectorRadius;
                float bell = t * t * (3f - 2f * t);
                if (bell > best) best = bell;
            }
            return best;
        }

        private float Value(float theta, float z) => Type switch
        {
            PatternType.Sine      => MathF.Sin(theta * Frequency),
            PatternType.Ripple    => MathF.Sin(z / Height * Frequency * TwoPi),
            PatternType.Guilloche => Guilloche(theta, z),
            PatternType.HWave     => HWave(theta, z),
            PatternType.VWave     => VWave(theta, z),
            PatternType.Pleats    => Pleats(theta),
            PatternType.Polygon   => Polygon(theta),
            PatternType.Diamond   => SmoothTriWave(theta * Frequency / TwoPi, 0.25f) * SmoothTriWave(z / CellMm, 0.25f),
            PatternType.Bumps     => Bumps(theta, z),
            PatternType.Bubbles   => Bubbles(theta, z),
            PatternType.Voronoi   => Voronoi(theta, z),
            PatternType.Hexagon   => Hexagon(theta, z),
            PatternType.Triangle  => Triangle(theta, z),
            PatternType.Hammered  => Hammered(theta, z),
            PatternType.Sunflower => Sunflower(theta, z),
            _ => 0f,
        };

        private float Guilloche(float theta, float z)
        {
            float k = z / Height * TwoPi * MathF.Max(1f, MathF.Round(Frequency * 0.6f));
            return 0.5f * (MathF.Sin(theta * Frequency + k) + MathF.Sin(theta * Frequency - k));
        }

        private float HWave(float theta, float z)
        {
            float band   = MathF.Max(2f, CellMm);
            float swings = MathF.Max(1f, MathF.Round(Frequency));
            float zShift = 0.6f * band * MathF.Sin(theta * swings);
            return MathF.Sin((z + zShift) / band * TwoPi);
        }

        private float VWave(float theta, float z)
        {
            float swings = MathF.Max(1f, MathF.Round(Frequency));
            float thetaShift = 0.6f * MathF.Sin(z / MathF.Max(2f, CellMm) * TwoPi);
            return MathF.Sin((theta + thetaShift) * swings);
        }

        private float Pleats(float theta)
        {
            float u = Frac(theta * Frequency / TwoPi);
            const float aPos = 0.22f;
            float v = u < aPos ? u / aPos : 1f - (u - aPos) / (1f - aPos);
            return v * 2f - 1f;
        }

        private float Polygon(float theta)
        {
            int n = Math.Max(3, (int)MathF.Round(Frequency));
            float sect = TwoPi / n;
            float a = ((theta % TwoPi) + TwoPi) % TwoPi;
            float local = a - MathF.Floor(a / sect) * sect - sect / 2f;
            float cosL = MathF.Cos(local), cosS = MathF.Cos(sect / 2f);
            float rawPoly = (cosS / cosL - 1f) / (1f - cosS);
            float cosTerm = MathF.Cos(MathF.PI * local / sect);
            return rawPoly * 0.6f + (-cosTerm * cosTerm) * 0.4f;
        }

        private float Bumps(float theta, float z)
        {
            float u = Frac(theta * Frequency / TwoPi);
            float v = Frac(z / CellMm);
            float du = u - 0.5f, dv = v - 0.5f;
            float d = MathF.Sqrt(du * du + dv * dv);
            return MathF.Cos(MathF.Min(d, 0.5f) * TwoPi);
        }

        private float Bubbles(float theta, float z)
        {
            float uRaw = theta * Frequency / TwoPi;
            float vRaw = z / CellMm;
            int rowIdx = (int)MathF.Floor(vRaw);
            const float bumpR = 0.75f;
            float maxBump = -1f;
            for (int dRow = -1; dRow <= 1; dRow++)
            {
                int r = rowIdx + dRow;
                float rowOffset = (r & 1) != 0 ? 0.5f : 0f;
                float dv = vRaw - (r + 0.5f);
                int kAnchor = (int)MathF.Round(uRaw - rowOffset);
                for (int dk = -1; dk <= 1; dk++)
                {
                    float du = uRaw - (kAnchor + dk + rowOffset);
                    du -= MathF.Round(du);
                    float dist = MathF.Sqrt(du * du + dv * dv);
                    if (dist < bumpR)
                    {
                        float bump = MathF.Cos(dist / bumpR * MathF.PI / 2f);
                        maxBump = MathF.Max(maxBump, 2f * bump * bump - 1f);
                    }
                }
            }
            return maxBump;
        }

        private float Voronoi(float theta, float z)
        {
            int n = Math.Max(3, (int)MathF.Round(Frequency));
            float u = Frac(theta / TwoPi) * n;
            float v = z / CellMm;
            int iu = (int)MathF.Floor(u), iv = (int)MathF.Floor(v);
            float f1 = 1e9f, f2 = 1e9f;
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    int ci = iu + di, cj = iv + dj;
                    int ciW = ((ci % n) + n) % n;
                    float sx = ci + 0.5f + 0.42f * VnHash(ciW, cj, 17);
                    float sy = cj + 0.5f + 0.42f * VnHash(ciW, cj, 53);
                    float dx = u - sx, dy = v - sy;
                    float d = dx * dx + dy * dy;
                    if (d < f1) { f2 = f1; f1 = d; }
                    else if (d < f2) f2 = d;
                }
            float edge = MathF.Sqrt(f2) - MathF.Sqrt(f1);
            return MathF.Min(1f, edge * 2.2f) * 2f - 1f;
        }

        private float Hexagon(float theta, float z)
        {
            int n = Math.Max(4, 2 * (int)MathF.Round(MathF.Max(2f, Frequency) / 2f));
            float rowH = CellMm * (MathF.Sqrt(3f) / 2f);
            float u = Frac(theta / TwoPi) * n;
            float v = z / rowH;
            int iu = (int)MathF.Floor(u), iv = (int)MathF.Floor(v);
            float f1 = 1e9f, f2 = 1e9f;
            for (int dj = -2; dj <= 2; dj++)
                for (int di = -2; di <= 2; di++)
                {
                    int ci = iu + di, cj = iv + dj;
                    int rowOdd = ((cj % 2) + 2) % 2;
                    float sx = ci + 0.5f + (rowOdd != 0 ? 0.5f : 0f);
                    float dx = u - sx;
                    dx -= MathF.Round(dx / n) * n;
                    float dy = v - (cj + 0.5f);
                    float d = dx * dx + dy * dy;
                    if (d < f1) { f2 = f1; f1 = d; }
                    else if (d < f2) f2 = d;
                }
            float hexEdge = MathF.Sqrt(f2) - MathF.Sqrt(f1);
            return MathF.Min(1f, hexEdge * 2.4f) * 2f - 1f;
        }

        private float Triangle(float theta, float z)
        {
            int n = Math.Max(3, (int)MathF.Round(MathF.Max(2f, Frequency)));
            float rowH = CellMm * (MathF.Sqrt(3f) / 2f);
            float u = Frac(theta / TwoPi) * n;
            float v = z / rowH;
            float a = u - v * 0.5f, b = v;
            float fa = a - MathF.Floor(a), fb = b - MathF.Floor(b);
            float w0, w1, w2;
            if (fa + fb < 1f) { w0 = 1f - fa - fb; w1 = fa; w2 = fb; }
            else              { w0 = 1f - fb; w1 = 1f - fa; w2 = fa + fb - 1f; }
            return MathF.Min(w0, MathF.Min(w1, w2)) * 6f - 1f;
        }

        private float Hammered(float theta, float z)
        {
            float q = MathF.Max(1f, Frequency) * 0.35f;
            float noise = ValueNoise3(MathF.Cos(theta) * q, MathF.Sin(theta) * q, z * (q / Radius));
            return Math.Clamp(noise * 1.6f, -1f, 1f);
        }

        // Sunflower / phyllotaxis: golden-angle seed points over the wall, cos² bumps.
        public void BuildSunflower()
        {
            int n = Math.Max(8, (int)MathF.Round(Frequency * Frequency * 0.8f));
            _sunflower = new (float, float)[n];
            const float golden = 2.399963f;   // golden angle (rad)
            for (int i = 0; i < n; i++)
                _sunflower[i] = (i * golden % TwoPi, (i + 0.5f) / n * Height);
            float area = TwoPi * Radius * Height;
            _sunBumpR = MathF.Sqrt(area / n) * 0.45f;
        }

        private float Sunflower(float theta, float z)
        {
            float best = -1f;
            foreach (var (st, sz) in _sunflower)
            {
                float dv = z - sz;
                if (MathF.Abs(dv) > _sunBumpR) continue;
                float dth = theta - st;
                dth -= MathF.Round(dth / TwoPi) * TwoPi;
                float du = dth * Radius;
                float dist = MathF.Sqrt(du * du + dv * dv);
                if (dist < _sunBumpR)
                {
                    float bump = MathF.Cos(dist / _sunBumpR * MathF.PI / 2f);
                    best = MathF.Max(best, 2f * bump * bump - 1f);
                }
            }
            return best;
        }

        // ── Helpers (ports of the effector noise/utility functions) ─────────

        private static float Frac(float x) => ((x % 1f) + 1f) % 1f;

        private static float SmoothTriWave(float x, float eps)
        {
            float t = Frac(x);
            float tri = MathF.Abs(t * 2f - 1f) * 2f - 1f;
            if (eps <= 0f) return tri;
            float cos = -MathF.Cos(TwoPi * t);
            return tri * (1f - eps) + cos * eps;
        }

        private static float VnHash(int ix, int iy, int iz)
        {
            int h = unchecked(ix * 374761393 + iy * 668265263 + iz * 1440662683);
            h = unchecked((h ^ (h >>> 13)) * 1274126177);
            h ^= h >>> 16;
            return (h & 65535) / 65535f * 2f - 1f;
        }

        private static float VnLayer(float x, float y, float z)
        {
            int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y), iz = (int)MathF.Floor(z);
            float fx = x - ix, fy = y - iy, fz = z - iz;
            float sx = fx * fx * (3f - 2f * fx), sy = fy * fy * (3f - 2f * fy), sz = fz * fz * (3f - 2f * fz);
            float c000 = VnHash(ix, iy, iz),     c100 = VnHash(ix + 1, iy, iz);
            float c010 = VnHash(ix, iy + 1, iz), c110 = VnHash(ix + 1, iy + 1, iz);
            float c001 = VnHash(ix, iy, iz + 1),     c101 = VnHash(ix + 1, iy, iz + 1);
            float c011 = VnHash(ix, iy + 1, iz + 1), c111 = VnHash(ix + 1, iy + 1, iz + 1);
            float x00 = c000 + (c100 - c000) * sx, x10 = c010 + (c110 - c010) * sx;
            float x01 = c001 + (c101 - c001) * sx, x11 = c011 + (c111 - c011) * sx;
            float y0 = x00 + (x10 - x00) * sy, y1 = x01 + (x11 - x01) * sy;
            return y0 + (y1 - y0) * sz;
        }

        private static float ValueNoise3(float x, float y, float z)
            => VnLayer(x, y, z) * 0.7f + VnLayer(x * 2.13f + 7.3f, y * 2.13f + 3.1f, z * 2.13f + 5.7f) * 0.3f;
    }
}
