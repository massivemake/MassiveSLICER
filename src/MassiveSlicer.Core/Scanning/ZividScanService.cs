using System.Runtime.InteropServices;

namespace MassiveSlicer.Core.Scanning;

/// <summary>
/// Result of a single Zivid capture: an organized point cloud in the camera
/// frame (millimetres), row-major with NaN entries for invalid pixels.
/// </summary>
public sealed class ScanCaptureResult
{
    /// <summary>Point grid width (columns).</summary>
    public required int Width { get; init; }

    /// <summary>Point grid height (rows).</summary>
    public required int Height { get; init; }

    /// <summary>
    /// Row-major XYZ triples, length Width × Height × 3, in camera-frame mm.
    /// Invalid pixels are NaN.
    /// </summary>
    public required float[] PointsXYZ { get; init; }

    /// <summary>Number of valid (non-NaN) points in the cloud.</summary>
    public required int ValidPointCount { get; init; }

    /// <summary>Full path of the saved .zdf file, or null when not saved.</summary>
    public string? SavedZdfPath { get; init; }
}

/// <summary>
/// Captures point clouds from the Zivid camera. Keeps the camera connection
/// open between captures; a failed capture resets the connection and retries
/// once so a power-cycled camera recovers transparently.
/// </summary>
public static class ZividScanService
{
    private const string ZividBinDir = @"C:\Program Files\Zivid\bin";

    private static readonly object Sync = new();
    private static Zivid.NET.Application? _app;
    private static Zivid.NET.Camera? _camera;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    static ZividScanService() => SetDllDirectory(ZividBinDir);

    /// <summary>
    /// Connects (or reuses the existing connection), captures one frame, and
    /// optionally saves it as a timestamped .zdf in <paramref name="saveDirectory"/>.
    /// Blocking — call from a worker thread. <paramref name="progress"/> receives
    /// human-readable phase updates and may be invoked from that thread.
    /// </summary>
    public static ScanCaptureResult Capture(string? saveDirectory, Action<string>? progress = null)
    {
        lock (Sync)
        {
            try
            {
                return CaptureOnce(saveDirectory, progress);
            }
            catch
            {
                // Stale connection (camera rebooted, network drop) — reconnect and retry once.
                ResetConnection();
                return CaptureOnce(saveDirectory, progress);
            }
        }
    }

    private static ScanCaptureResult CaptureOnce(string? saveDirectory, Action<string>? progress)
    {
        _app ??= new Zivid.NET.Application();

        if (_camera is null)
        {
            progress?.Invoke("Connecting to camera...");
            _camera = _app.ConnectCamera();
        }

        var settings = new Zivid.NET.Settings();
        settings.Acquisitions.Add(new Zivid.NET.Settings.Acquisition());

        progress?.Invoke("Capturing...");
        using var frame = _camera.Capture(settings);
        var pointCloud  = frame.PointCloud;

        int width  = checked((int)pointCloud.Width);
        int height = checked((int)pointCloud.Height);

        // float[height, width, 3] — multidimensional arrays are contiguous, so a
        // single block copy flattens to row-major XYZ triples.
        var grid = pointCloud.CopyPointsXYZ();
        var flat = new float[width * height * 3];
        Buffer.BlockCopy(grid, 0, flat, 0, flat.Length * sizeof(float));

        int valid = 0;
        for (int i = 0; i < flat.Length; i += 3)
            if (!float.IsNaN(flat[i])) valid++;

        string? zdfPath = null;
        if (!string.IsNullOrWhiteSpace(saveDirectory))
        {
            progress?.Invoke("Saving .zdf...");
            var dir = Path.GetFullPath(saveDirectory);
            Directory.CreateDirectory(dir);
            zdfPath = Path.Combine(dir, $"scan_{DateTime.Now:yyyyMMdd_HHmmss}.zdf");
            frame.Save(zdfPath);
        }

        return new ScanCaptureResult
        {
            Width           = width,
            Height          = height,
            PointsXYZ       = flat,
            ValidPointCount = valid,
            SavedZdfPath    = zdfPath,
        };
    }

    private static void ResetConnection()
    {
        try { _camera?.Disconnect(); } catch { }
        try { _camera?.Dispose(); }    catch { }
        _camera = null;
    }
}
