namespace MassiveSlicer.Core.Scanning;

/// <summary>Axis-aligned box of the rough part, in millimetres, world Z up.</summary>
public readonly record struct MagicScanBox(
    float MinX, float MinY, float MinZ,
    float MaxX, float MaxY, float MaxZ,
    int PointCount)
{
    public float SizeX => MaxX - MinX;
    public float SizeY => MaxY - MinY;
    public float Top => MaxZ;
    public float CenterX => (MinX + MaxX) * 0.5f;
    public float CenterY => (MinY + MaxY) * 0.5f;
}

/// <summary>One camera station above the part. The camera looks down. Joints are not solved.</summary>
public readonly record struct MagicScanStation(float X, float Y, float Z);

/// <summary>
/// Coarse-scan decisions for Magic Scan. The table is ignored. A fine path is a
/// look-down grid at a fixed standoff above the detected box, never inside it.
/// </summary>
public static class MagicScanPlanner
{
    public const int MaxStations = 40;

    /// <summary>
    /// Box of points above <paramref name="bedZ"/> + <paramref name="clearanceMm"/>.
    /// <paramref name="xyz"/> is interleaved x,y,z. Returns null when fewer than
    /// <paramref name="minPoints"/> points clear the table.
    /// </summary>
    public static MagicScanBox? Detect(float[]? xyz, float bedZ, float clearanceMm, int minPoints = 30)
    {
        if (xyz is null || xyz.Length < 3 || minPoints < 1) return null;
        float floor = bedZ + MathF.Max(0f, clearanceMm);
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        int count = 0;
        int n = xyz.Length - (xyz.Length % 3);
        for (int i = 0; i + 2 < n; i += 3)
        {
            float x = xyz[i], y = xyz[i + 1], z = xyz[i + 2];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) continue;
            if (z <= floor) continue;
            count++;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (z < minZ) minZ = z;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            if (z > maxZ) maxZ = z;
        }

        if (count < minPoints) return null;
        return new MagicScanBox(minX, minY, minZ, maxX, maxY, maxZ, count);
    }

    /// <summary>True when the part is larger than one overhead camera field.</summary>
    public static bool NeedsSecondOverhead(MagicScanBox box, float fieldMm)
    {
        float field = MathF.Max(50f, fieldMm);
        return box.SizeX > field || box.SizeY > field;
    }

    /// <summary>
    /// Look-down stations that cover the box. Z is the top of the part plus standoff.
    /// Spacing comes from the footprint and overlap. The count stays at or under
    /// <see cref="MaxStations"/> by widening the step.
    /// </summary>
    public static IReadOnlyList<MagicScanStation> PlanFine(
        MagicScanBox box, float standoffMm, float footprintMm, float overlap)
    {
        float standoff = Math.Clamp(standoffMm, 50f, 2000f);
        float footprint = Math.Clamp(footprintMm, 50f, 2000f);
        float cover = Math.Clamp(overlap, 0f, 0.8f);
        float spacing = MathF.Max(20f, footprint * (1f - cover));
        float z = box.Top + standoff;

        int nx = 1, ny = 1;
        for (int widen = 0; widen < 24; widen++)
        {
            nx = Steps(box.SizeX, footprint, spacing);
            ny = Steps(box.SizeY, footprint, spacing);
            if (nx * ny <= MaxStations) break;
            spacing *= 1.25f;
        }

        var stations = new List<MagicScanStation>(nx * ny);
        for (int iy = 0; iy < ny; iy++)
        {
            float y = Along(box.MinY, box.MaxY, ny, iy);
            for (int ix = 0; ix < nx; ix++)
            {
                float x = Along(box.MinX, box.MaxX, nx, ix);
                stations.Add(new MagicScanStation(x, y, z));
            }
        }

        return stations;
    }

    /// <summary>
    /// Height above the part for the first rough pass. High enough that one
    /// frame can see the whole span, and at least 400 mm above the fine standoff.
    /// </summary>
    public static float RaisedStandoffMm(MagicScanBox box, float fineStandoffMm, float fovDegrees = 70f)
    {
        float span = MathF.Max(1f, MathF.Max(box.SizeX, box.SizeY));
        float half = Math.Clamp(fovDegrees, 20f, 120f) * 0.5f * (MathF.PI / 180f);
        float fit = span * 0.62f / MathF.Max(0.2f, MathF.Tan(half));
        float raised = MathF.Max(fit, Math.Clamp(fineStandoffMm, 50f, 2000f) + 400f);
        return Math.Clamp(raised, 900f, 1800f);
    }

    /// <summary>
    /// Heights to try, tallest first. The last one is still a raised view.
    /// A miss at the first height can retry lower before giving up.
    /// </summary>
    public static IReadOnlyList<float> StandoffAttempts(float raisedMm, float fineMm)
    {
        float raised = Math.Clamp(raisedMm, 300f, 2200f);
        float mid = Math.Clamp(MathF.Max(fineMm + 250f, raised * 0.72f), 400f, raised);
        float low = Math.Clamp(MathF.Max(fineMm, 450f), 300f, mid);
        var list = new List<float>(3);
        Add(raised);
        Add(mid);
        Add(low);
        return list;

        void Add(float value)
        {
            if (list.Exists(x => MathF.Abs(x - value) < 20f)) return;
            list.Add(value);
        }
    }

    /// <summary>
    /// Fine-pass height. Uses the requested standoff when it is clearly below
    /// the rough pass. Otherwise it sits at about half the rough height.
    /// </summary>
    public static float CloserStandoffMm(float coarseStandoffMm, float requestedFineMm)
    {
        float coarse = Math.Clamp(coarseStandoffMm, 200f, 2200f);
        float requested = Math.Clamp(requestedFineMm, 150f, 2000f);
        if (requested <= coarse - 150f)
            return requested;
        return MathF.Max(150f, coarse * 0.55f);
    }

    /// <summary>
    /// Look-down grid above the whole box. A part that fits in one frame still
    /// gets a 2×2 pulled in from the edges, so the rough pass is a grid and
    /// every photo still sees the middle. Count stays at or under 9.
    /// </summary>
    public static IReadOnlyList<MagicScanStation> PlanCoarse(
        MagicScanBox box, float standoffMm, float fovDegrees = 70f)
    {
        float standoff = Math.Clamp(standoffMm, 200f, 2200f);
        float z = box.Top + standoff;
        float half = Math.Clamp(fovDegrees, 20f, 120f) * 0.5f * (MathF.PI / 180f);
        float visible = MathF.Max(50f, 2f * standoff * MathF.Tan(half));
        float cover = visible * 0.7f;
        int nx = CoarseCount(box.SizeX, cover);
        int ny = CoarseCount(box.SizeY, cover);
        while (nx * ny > 9)
        {
            if (nx >= ny && nx > 1) nx--;
            else if (ny > 1) ny--;
            else break;
        }

        bool insetX = box.SizeX <= cover && nx > 1;
        bool insetY = box.SizeY <= cover && ny > 1;
        var stations = new List<MagicScanStation>(nx * ny);
        for (int iy = 0; iy < ny; iy++)
        {
            float y = Spread(box.MinY, box.MaxY, ny, iy, insetY);
            for (int ix = 0; ix < nx; ix++)
            {
                float x = Spread(box.MinX, box.MaxX, nx, ix, insetX);
                stations.Add(new MagicScanStation(x, y, z));
            }
        }

        return stations;
    }

    /// <summary>Axis-aligned bounds of world triangles (9 floats each). Null when empty.</summary>
    public static MagicScanBox? BoundsOfTriangles(float[]? triangles)
    {
        if (triangles is null || triangles.Length < 9) return null;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        int count = 0;
        int n = triangles.Length - (triangles.Length % 3);
        for (int i = 0; i + 2 < n; i += 3)
        {
            float x = triangles[i], y = triangles[i + 1], z = triangles[i + 2];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) continue;
            count++;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (z < minZ) minZ = z;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            if (z > maxZ) maxZ = z;
        }

        if (count < 3) return null;
        return new MagicScanBox(minX, minY, minZ, maxX, maxY, maxZ, count);
    }

    static int CoarseCount(float size, float cover)
    {
        if (size <= cover) return size > 150f ? 2 : 1;
        int n = (int)MathF.Ceiling(size / MathF.Max(1f, cover * 0.55f));
        return Math.Clamp(n, 2, 3);
    }

    static float Spread(float min, float max, int count, int index, bool inset)
    {
        if (count <= 1) return (min + max) * 0.5f;
        float t = index / (float)(count - 1);
        if (inset) t = 0.25f + 0.50f * t;
        return min + (max - min) * t;
    }

    static int Steps(float size, float footprint, float spacing)
    {
        if (size <= footprint) return 1;
        return Math.Max(1, (int)MathF.Ceiling((size - footprint) / spacing) + 1);
    }

    static float Along(float min, float max, int count, int index)
    {
        if (count <= 1) return (min + max) * 0.5f;
        float t = index / (float)(count - 1);
        return min + (max - min) * t;
    }
}
