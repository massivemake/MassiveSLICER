namespace MassiveSlicer.Core.Scanning;

/// <summary>
/// Result of a single Zivid capture: an organized point cloud in the camera
/// frame (millimetres), row-major with NaN entries for invalid pixels.
/// </summary>
public sealed class ScanCaptureResult
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required float[] PointsXYZ { get; init; }
    public required int ValidPointCount { get; init; }
    public string? SavedZdfPath { get; init; }
}

/// <summary>
/// Stub — Zivid SDK is Windows-only. Throws on any non-Windows platform.
/// </summary>
public static class ZividScanService
{
    public static ScanCaptureResult Capture(string? saveDirectory, Action<string>? progress = null)
        => throw new PlatformNotSupportedException("Zivid SDK is only available on Windows.");
}
