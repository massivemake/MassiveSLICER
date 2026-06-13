using System.Diagnostics;
using MassiveSlicer.Core.Scanning;
using MassiveSlicer.Viewport.Loading;

// Exercises the same capture + meshing pipeline the app's Test Scan button uses.
var outputDir = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "scans");

var sw = Stopwatch.StartNew();
var result = ZividScanService.Capture(outputDir, msg => Console.WriteLine($"  [{sw.Elapsed.TotalSeconds:F1}s] {msg}"));
Console.WriteLine($"Captured {result.Width} x {result.Height}: {result.ValidPointCount:N0} valid points in {sw.Elapsed.TotalSeconds:F1}s");
if (result.SavedZdfPath is not null)
    Console.WriteLine($"Saved {result.SavedZdfPath}");

sw.Restart();
var node = PointCloudMesher.Build(result.PointsXYZ, result.Width, result.Height, "Scan");
if (node?.PendingMesh is not { } mesh)
{
    Console.WriteLine("Meshing produced no geometry!");
    return 1;
}
Console.WriteLine($"Meshed {mesh.Positions.Length:N0} vertices, {mesh.Indices!.Length / 3:N0} triangles in {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"Bounds: min {mesh.LocalBounds.Min}, max {mesh.LocalBounds.Max} (mm, camera frame)");

// Second capture re-uses the held connection — should skip the connect phase.
sw.Restart();
var result2 = ZividScanService.Capture(null, msg => Console.WriteLine($"  [{sw.Elapsed.TotalSeconds:F1}s] {msg}"));
Console.WriteLine($"Second capture (reused connection): {result2.ValidPointCount:N0} valid points in {sw.Elapsed.TotalSeconds:F1}s");
return 0;
