using MassiveSlicer.Core.Scanning;

static List<(double X, double Y)> LoadPlan(
    string path, double cx, double cy, double e1, double sign, double zTop, double zBand = 8)
{
    var plan = new List<(double, double)>();
    const double r2d = Math.PI / 180.0;
    double ang = sign * e1 * r2d;
    double ca = Math.Cos(-ang), sa = Math.Sin(-ang);

    foreach (var line in File.ReadLines(path))
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) continue;
        if (!double.TryParse(p[0], out double x)) continue;
        if (!double.TryParse(p[1], out double y)) continue;
        if (!double.TryParse(p[2], out double z)) continue;
        if (z < zTop - 30 || z > zTop + zBand) continue;
        double dx = x - cx, dy = y - cy;
        if (dx * dx + dy * dy > 880 * 880) continue;
        plan.Add((ca * dx - sa * dy, sa * dx + ca * dy));
    }
    return plan;
}

static double ZTop(string path)
{
    var zs = new List<double>();
    foreach (var line in File.ReadLines(path))
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 3 && double.TryParse(p[2], out double z)) zs.Add(z);
    }
    zs.Sort();
    return zs.Count > 0 ? zs[(int)(zs.Count * 0.55)] : 890;
}

var diagDir = args.Length > 0 ? args[0]
    : @"C:\Users\MassiveMAKE\AppData\Local\MassiveSlicer\build\scans\diag";
var manifestPath = Path.Combine(diagDir, "manifest.json");
if (!File.Exists(manifestPath)) { Console.WriteLine("No manifest"); return 1; }

using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
var root = doc.RootElement;
var centre = root.GetProperty("rotaryCenter");
double manifestCx = centre[0].GetDouble(), manifestCy = centre[1].GetDouble();
double sign = root.GetProperty("rotationSign").GetDouble();

// Optional JSON bed.origin override: AnalyzeDiagAlignment.exe <diagDir> [cx cy]
double cx = manifestCx, cy = manifestCy;
if (args.Length >= 3 &&
    double.TryParse(args[1], System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var ocx) &&
    double.TryParse(args[2], System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var ocy))
{
    cx = ocx; cy = ocy;
    Console.WriteLine($"Centre: ({cx:F2}, {cy:F2})  [override]  manifest=({manifestCx:F2}, {manifestCy:F2})  sign={sign:F0}");
}
else
    Console.WriteLine($"Centre: ({cx:F2}, {cy:F2})  sign={sign:F0}");
Console.WriteLine();

foreach (var scan in root.GetProperty("scans").EnumerateArray())
{
    string file = scan.GetProperty("file").GetString()!;
    double e1 = scan.GetProperty("e1").GetDouble();
    string path = Path.Combine(diagDir, file);
    if (!File.Exists(path)) continue;

    double ztop = ZTop(path);
    var plan = LoadPlan(path, cx, cy, e1, sign, ztop);
    var angle = RotaryPhaseEstimator.HoleLatticeAngleDeg(plan, out int holes);
    string tag = file.Contains("scan_") ? "TEST" : "bedcal";
    Console.WriteLine($"{tag,-6} {file,-28} E1={e1,7:F1}°  holes={holes,4}  lattice={angle?.ToString("F3") ?? "null"}°  pts={plan.Count}");
}

// Pooled phase fit (same as EstimateAndApplyBedPhaseAsync)
string? tagFilter = args.Length > 3 ? args[3] : null; // e.g. "Y0" or "scan_"
var pooled = new List<(double, double)>();
foreach (var scan in root.GetProperty("scans").EnumerateArray())
{
    string file = scan.GetProperty("file").GetString()!;
    if (!file.Contains("bedcal")) continue;
    if (tagFilter is not null && !file.Contains(tagFilter, StringComparison.OrdinalIgnoreCase)) continue;
    double e1 = scan.GetProperty("e1").GetDouble();
    string path = Path.Combine(diagDir, file);
    if (!File.Exists(path)) continue;
    double ztop = ZTop(path);
    foreach (var pt in LoadPlan(path, cx, cy, e1, sign, ztop))
        pooled.Add(pt);
}
var pooledAngle = RotaryPhaseEstimator.HoleLatticeAngleDeg(pooled, out int pooledHoles);
Console.WriteLine();
string poolLabel = tagFilter is null ? "bedcal" : $"bedcal[{tagFilter}]";
Console.WriteLine($"Pooled {poolLabel} ({pooled.Count} pts, {pooledHoles} holes): lattice={pooledAngle?.ToString("F3") ?? "null"}°");
Console.WriteLine($"Suggested orientationOffsetDeg ≈ {(pooledAngle ?? 0):F3}° (bed-orient / lfam3.json rotaryBed.orientationOffsetDeg)");
Console.WriteLine();
Console.WriteLine("Model bed holes are world-axis-aligned (expect lattice ≈ 0° after correct phase).");
return 0;