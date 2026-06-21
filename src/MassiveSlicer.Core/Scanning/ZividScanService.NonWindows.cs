using System.Numerics;

namespace MassiveSlicer.Core.Scanning;

/// <summary>Result of a single calibration-board detection attempt.</summary>
public sealed class BoardDetectionResult
{
    public required bool Detected { get; init; }
    public required string Status { get; init; }
}

/// <summary>Result of a completed hand-eye calibration run.</summary>
public sealed class HandEyeCalibResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public float TcpX { get; init; }
    public float TcpY { get; init; }
    public float TcpZ { get; init; }
    public float TcpA { get; init; }
    public float TcpB { get; init; }
    public float TcpC { get; init; }
    public float AvgRotResidualDeg { get; init; }
    public float AvgTransResidualMm { get; init; }
}

/// <summary>
/// Robot and tool state snapshot recorded at the moment of a scan capture.
/// </summary>
public sealed class ScanMetadata
{
    public float A1 { get; init; }
    public float A2 { get; init; }
    public float A3 { get; init; }
    public float A4 { get; init; }
    public float A5 { get; init; }
    public float A6 { get; init; }
    public float E1 { get; init; }
    public float TcpX { get; init; }
    public float TcpY { get; init; }
    public float TcpZ { get; init; }
    public float TcpA { get; init; }
    public float TcpB { get; init; }
    public float TcpC { get; init; }
    public float CameraWorldX { get; init; }
    public float CameraWorldY { get; init; }
    public float CameraWorldZ { get; init; }
}

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
    public string? SavedMetadataPath { get; init; }
    public ScanMetadata? Metadata { get; init; }
}

/// <summary>
/// Stub — Zivid SDK is Windows-only. Throws on any non-Windows platform.
/// </summary>
public static class ZividScanService
{
    private static PlatformNotSupportedException Unsupported()
        => new("Zivid SDK is only available on Windows.");

    public static ScanCaptureResult Capture(
        string? saveDirectory,
        ScanMetadata? metadata = null,
        Action<string>? progress = null)
        => throw Unsupported();

    public static void Disconnect() { }

    public sealed class BoardCentroidResult
    {
        public required bool Detected { get; init; }
        public required string Status { get; init; }
        public float X { get; init; }
        public float Y { get; init; }
        public float Z { get; init; }
    }

    public static BoardCentroidResult DetectBoardCentroid(Action<string>? progress = null)
        => throw Unsupported();

    public static int CalibrationPoseCount => 0;

    public static void ClearCalibrationPoses() { }

    public static BoardDetectionResult AddCalibrationPose(
        Matrix4x4 flangeInBase,
        Action<string>? progress = null)
        => throw Unsupported();

    public static HandEyeCalibResult RunHandEyeCalibration()
        => throw Unsupported();
}