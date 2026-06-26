namespace MassiveSlicer.Core.Scanning;

/// <summary>
/// Measures the in-plane orientation of a flat perforated plate (the rotary bed top) from its
/// surface points, by locating the hole pattern and fitting the hole lattice angle. Used by the
/// auto bed calibration to recover the constant rotational phase between the model and reality:
/// the bed's hole grid is world-axis-aligned in the model, so the lattice angle of the scanned
/// (un-rotated to E1=0) surface, minus the model mesh's lattice angle, is the orientation offset.
///
/// Pure geometry (no external deps): rasterise → enclosed-empty (holes) → connected components →
/// hole centroids → dominant nearest-neighbour lattice angle. Insensitive parts (the flat surface)
/// contribute nothing; the holes carry the rotational signal.
/// </summary>
public static class RotaryPhaseEstimator
{
    /// <summary>
    /// Hole-lattice angle in degrees, folded to (−45, 45] (deviation from the X/Y axes), or null if
    /// too few holes are found. <paramref name="pts"/> are in-plane points (mm), already projected to
    /// the bed plane and centred near the origin.
    /// </summary>
    public static double? HoleLatticeAngleDeg(
        IReadOnlyList<(double X, double Y)> pts,
        out int holeCount,
        double cellMm = 3.0, double maxRadiusMm = 880.0,
        double holeMinMm = 10.0, double holeMaxMm = 70.0,
        double spacingMinMm = 40.0, double spacingMaxMm = 140.0)
    {
        holeCount = 0;
        if (pts.Count < 500) return null;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in pts)
        {
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        minX -= cellMm * 3; minY -= cellMm * 3; maxX += cellMm * 3; maxY += cellMm * 3;
        int nx = (int)((maxX - minX) / cellMm) + 1;
        int ny = (int)((maxY - minY) / cellMm) + 1;
        if (nx < 8 || ny < 8 || (long)nx * ny > 40_000_000) return null;

        var occ = new bool[nx, ny];
        foreach (var (x, y) in pts)
        {
            int i = (int)((x - minX) / cellMm), j = (int)((y - minY) / cellMm);
            if ((uint)i < (uint)nx && (uint)j < (uint)ny) occ[i, j] = true;
        }

        // Close 1-cell sampling gaps so they don't read as holes.
        var closed = Erode(Dilate(occ, nx, ny), nx, ny);

        // Enclosed empty = holes: empty cells not reachable from the border through empty cells.
        var outside = FloodFromBorder(closed, nx, ny);   // border-connected empty
        var holes = new bool[nx, ny];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
                holes[i, j] = !closed[i, j] && !outside[i, j];

        int holeMinCells = (int)(Math.PI * Math.Pow(holeMinMm / 2 / cellMm, 2));
        int holeMaxCells = (int)(Math.PI * Math.Pow(holeMaxMm / 2 / cellMm, 2)) + 4;
        var cents = Centroids(holes, nx, ny, holeMinCells, holeMaxCells,
                              minX, minY, cellMm, maxRadiusMm);
        holeCount = cents.Count;
        if (cents.Count < 8) return null;

        return LatticeAngle(cents, spacingMinMm, spacingMaxMm);
    }

    private static bool[,] Dilate(bool[,] m, int nx, int ny)
    {
        var o = new bool[nx, ny];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
                if (m[i, j])
                {
                    o[i, j] = true;
                    if (i > 0) o[i - 1, j] = true;
                    if (i < nx - 1) o[i + 1, j] = true;
                    if (j > 0) o[i, j - 1] = true;
                    if (j < ny - 1) o[i, j + 1] = true;
                }
        return o;
    }

    private static bool[,] Erode(bool[,] m, int nx, int ny)
    {
        var o = new bool[nx, ny];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
                o[i, j] = m[i, j]
                          && (i == 0 || m[i - 1, j]) && (i == nx - 1 || m[i + 1, j])
                          && (j == 0 || m[i, j - 1]) && (j == ny - 1 || m[i, j + 1]);
        return o;
    }

    // BFS flood of empty (!filled) cells starting from every border empty cell.
    private static bool[,] FloodFromBorder(bool[,] filled, int nx, int ny)
    {
        var vis = new bool[nx, ny];
        var stack = new Stack<(int, int)>();
        void Push(int i, int j) { if ((uint)i < (uint)nx && (uint)j < (uint)ny && !filled[i, j] && !vis[i, j]) { vis[i, j] = true; stack.Push((i, j)); } }
        for (int i = 0; i < nx; i++) { Push(i, 0); Push(i, ny - 1); }
        for (int j = 0; j < ny; j++) { Push(0, j); Push(nx - 1, j); }
        while (stack.Count > 0)
        {
            var (i, j) = stack.Pop();
            Push(i - 1, j); Push(i + 1, j); Push(i, j - 1); Push(i, j + 1);
        }
        return vis;
    }

    private static List<(double X, double Y)> Centroids(
        bool[,] holes, int nx, int ny, int minCells, int maxCells,
        double minX, double minY, double cellMm, double maxRadiusMm)
    {
        var result = new List<(double, double)>();
        var vis = new bool[nx, ny];
        var stack = new Stack<(int, int)>();
        for (int si = 0; si < nx; si++)
            for (int sj = 0; sj < ny; sj++)
            {
                if (!holes[si, sj] || vis[si, sj]) continue;
                vis[si, sj] = true; stack.Push((si, sj));
                long sumI = 0, sumJ = 0; int area = 0;
                while (stack.Count > 0)
                {
                    var (i, j) = stack.Pop();
                    sumI += i; sumJ += j; area++;
                    void P(int a, int b) { if ((uint)a < (uint)nx && (uint)b < (uint)ny && holes[a, b] && !vis[a, b]) { vis[a, b] = true; stack.Push((a, b)); } }
                    P(i - 1, j); P(i + 1, j); P(i, j - 1); P(i, j + 1);
                }
                if (area < minCells || area > maxCells) continue;
                double cx = minX + (sumI / (double)area + 0.5) * cellMm;
                double cy = minY + (sumJ / (double)area + 0.5) * cellMm;
                if (cx * cx + cy * cy <= maxRadiusMm * maxRadiusMm) result.Add((cx, cy));
            }
        return result;
    }

    private static double LatticeAngle(List<(double X, double Y)> c, double sMin, double sMax)
    {
        const double R2D = 180.0 / Math.PI;
        var devs = new List<double>();
        for (int i = 0; i < c.Count; i++)
            for (int k = 0; k < c.Count; k++)
            {
                if (k == i) continue;
                double dx = c[k].X - c[i].X, dy = c[k].Y - c[i].Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < sMin || d > sMax) continue;
                double a = (Math.Atan2(dy, dx) * R2D % 90 + 90) % 90;   // [0,90)
                devs.Add(((a + 45) % 90) - 45);                         // (-45,45]
            }
        if (devs.Count < 8) return double.NaN;

        // Robust peak: 1° histogram, then mean within ±3° of the densest bin.
        var hist = new int[91];
        foreach (var dv in devs) hist[(int)Math.Round(dv) + 45]++;
        int peak = 0; for (int b = 1; b < 91; b++) if (hist[b] > hist[peak]) peak = b;
        double peakDev = peak - 45;
        double sum = 0; int n = 0;
        foreach (var dv in devs) if (Math.Abs(dv - peakDev) < 3) { sum += dv; n++; }
        return n > 0 ? sum / n : peakDev;
    }
}
