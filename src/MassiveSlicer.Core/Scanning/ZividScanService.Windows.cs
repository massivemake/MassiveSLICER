using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MassiveSlicer.Core.Kinematics;

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
    /// <summary>Camera-origin in flange frame (mm).</summary>
    public float TcpX { get; init; }
    public float TcpY { get; init; }
    public float TcpZ { get; init; }
    /// <summary>KUKA ZYX Euler angles (°) for the camera frame in the flange frame.</summary>
    public float TcpA { get; init; }
    public float TcpB { get; init; }
    public float TcpC { get; init; }
    public float AvgRotResidualDeg { get; init; }
    public float AvgTransResidualMm { get; init; }
}

/// <summary>
/// Robot and tool state snapshot recorded at the moment of a scan capture.
/// Saved as a JSON sidecar alongside the .zdf file.
/// </summary>
public sealed class ScanMetadata
{
    // Robot axes (KRL degrees) ------------------------------------------------
    public float A1 { get; init; }
    public float A2 { get; init; }
    public float A3 { get; init; }
    public float A4 { get; init; }
    public float A5 { get; init; }
    public float A6 { get; init; }
    /// <summary>External axis 1 — rotary print bed (KRL degrees).</summary>
    public float E1 { get; init; }

    // Active TCP offset (scanner TOOL_DATA, mm / KRL degrees) -----------------
    public float TcpX { get; init; }
    public float TcpY { get; init; }
    public float TcpZ { get; init; }
    public float TcpA { get; init; }
    public float TcpB { get; init; }
    public float TcpC { get; init; }

    // Camera origin in world / ROBROOT frame (mm) -----------------------------
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

    /// <summary>Full path of the saved .json sidecar, or null when not saved.</summary>
    public string? SavedMetadataPath { get; init; }

    /// <summary>Robot and tool state at capture time.</summary>
    public ScanMetadata? Metadata { get; init; }
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
    /// optionally saves a timestamped .zdf + JSON sidecar in <paramref name="saveDirectory"/>.
    /// Blocking — call from a worker thread. <paramref name="progress"/> receives
    /// human-readable phase updates and may be invoked from that thread.
    /// </summary>
    public static ScanCaptureResult Capture(
        string? saveDirectory,
        ScanMetadata? metadata = null,
        Action<string>? progress = null)
    {
        lock (Sync)
        {
            try
            {
                return CaptureOnce(saveDirectory, metadata, progress);
            }
            catch (InvalidOperationException)
            {
                // Camera busy / not found — retrying won't help, propagate immediately.
                throw;
            }
            catch
            {
                // Stale connection (camera rebooted, network drop) — reconnect and retry once.
                ResetConnection();
                return CaptureOnce(saveDirectory, metadata, progress);
            }
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private static ScanCaptureResult CaptureOnce(
        string? saveDirectory,
        ScanMetadata? metadata,
        Action<string>? progress)
    {
        _app ??= new Zivid.NET.Application();

        if (_camera is null)
        {
            progress?.Invoke("Connecting to camera...");
            try
            {
                _camera = _app.ConnectCamera();
            }
            catch (Exception ex) when (ex.Message.Contains("busy", StringComparison.OrdinalIgnoreCase)
                                    || ex.Message.Contains("No cameras found with status Available", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Camera is in use by another application (e.g. Zivid Studio). Close it and try again.", ex);
            }
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

        string? zdfPath      = null;
        string? metadataPath = null;
        if (!string.IsNullOrWhiteSpace(saveDirectory))
        {
            progress?.Invoke("Saving .zdf...");
            var dir       = Path.GetFullPath(saveDirectory);
            Directory.CreateDirectory(dir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            zdfPath       = Path.Combine(dir, $"scan_{timestamp}.zdf");
            frame.Save(zdfPath);

            if (metadata is not null)
            {
                metadataPath = Path.Combine(dir, $"scan_{timestamp}.json");
                var jsonData = new
                {
                    Timestamp = DateTime.Now.ToString("o"),
                    Joints    = new { metadata.A1, metadata.A2, metadata.A3,
                                      metadata.A4, metadata.A5, metadata.A6, metadata.E1 },
                    Tcp       = new { X = metadata.TcpX, Y = metadata.TcpY, Z = metadata.TcpZ,
                                      A = metadata.TcpA, B = metadata.TcpB, C = metadata.TcpC },
                    CameraWorldMm = new { X = metadata.CameraWorldX,
                                          Y = metadata.CameraWorldY,
                                          Z = metadata.CameraWorldZ },
                };
                File.WriteAllText(metadataPath, JsonSerializer.Serialize(jsonData, _jsonOptions));
            }
        }

        return new ScanCaptureResult
        {
            Width               = width,
            Height              = height,
            PointsXYZ           = flat,
            ValidPointCount     = valid,
            SavedZdfPath        = zdfPath,
            SavedMetadataPath   = metadataPath,
            Metadata            = metadata,
        };
    }

    /// <summary>
    /// Disconnects and releases the camera and SDK application instance.
    /// Call on app shutdown to ensure the camera is not left in a busy state.
    /// </summary>
    public static void Disconnect()
    {
        lock (Sync)
        {
            ResetConnection();
            try { _app?.Dispose(); } catch { }
            _app = null;
        }
    }

    private static void ResetConnection()
    {
        try { _camera?.Disconnect(); } catch { }
        try { _camera?.Dispose(); }    catch { }
        _camera = null;
    }

    // -- Rotary-bed calibration ---------------------------------------------

    /// <summary>Result of a single rotary-bed marker detection.</summary>
    public sealed class BoardCentroidResult
    {
        public required bool Detected { get; init; }
        public required string Status { get; init; }
        /// <summary>Board centroid in the camera frame (mm). Valid only when <see cref="Detected"/>.</summary>
        public float X { get; init; }
        public float Y { get; init; }
        public float Z { get; init; }
    }

    /// <summary>
    /// Captures and detects the calibration board and returns its centroid in the
    /// camera frame (mm). Used by rotary-bed calibration: with the arm held still and
    /// the board fixed to the bed, the centroid sweeps a circle about the E1 axis.
    /// Blocking — call from a worker thread.
    /// </summary>
    public static BoardCentroidResult DetectBoardCentroid(Action<string>? progress = null)
    {
        lock (Sync)
        {
            try
            {
                _app ??= new Zivid.NET.Application();
                if (_camera is null)
                {
                    progress?.Invoke("Connecting to camera...");
                    _camera = _app.ConnectCamera();
                }

                progress?.Invoke("Capturing calibration board...");
                Zivid.NET.Frame frame;
                try
                {
                    frame = Zivid.NET.Calibration.Detector.CaptureCalibrationBoard(_camera);
                }
                catch
                {
                    ResetConnection();
                    _camera = _app.ConnectCamera();
                    frame = Zivid.NET.Calibration.Detector.CaptureCalibrationBoard(_camera);
                }

                progress?.Invoke("Detecting board...");
                using (frame)
                using (var detection = Zivid.NET.Calibration.Detector.DetectCalibrationBoard(frame))
                {
                    if (!detection.Valid())
                        return new BoardCentroidResult { Detected = false, Status = $"Board not detected: {detection.StatusDescription()}" };

                    var c = detection.Centroid();
                    return new BoardCentroidResult
                    {
                        Detected = true,
                        Status   = "Board detected",
                        X = c.x, Y = c.y, Z = c.z,
                    };
                }
            }
            catch (Exception ex)
            {
                ResetConnection();
                return new BoardCentroidResult { Detected = false, Status = $"Camera error: {ex.Message}" };
            }
        }
    }

    // -- Hand-eye calibration -----------------------------------------------

    // Accumulated pose captures; each holds a DetectionResult that must be
    // kept alive until after RunHandEyeCalibration() is called.
    private static readonly List<(Matrix4x4 FlangeInBase, Zivid.NET.Calibration.DetectionResult Detection)>
        _calibPoses = new();

    public static int CalibrationPoseCount { get { lock (Sync) return _calibPoses.Count; } }

    /// <summary>Dispose all stored board detections and reset the pose list.</summary>
    public static void ClearCalibrationPoses()
    {
        lock (Sync)
        {
            foreach (var (_, d) in _calibPoses)
                try { d.Dispose(); } catch { }
            _calibPoses.Clear();
        }
    }

    /// <summary>
    /// Captures the calibration board using the camera's optimized capture settings,
    /// detects it, and — if successful — appends the (flangeInBase, detection) pair to
    /// the internal list for a later <see cref="RunHandEyeCalibration"/> call.
    /// <paramref name="flangeInBase"/> should be <c>KukaIkSolver.ForwardKinematics(krl)</c>
    /// (row-vector convention; the method transposes it internally for the Zivid Pose).
    /// </summary>
    public static BoardDetectionResult AddCalibrationPose(
        Matrix4x4 flangeInBase,
        Action<string>? progress = null)
    {
        lock (Sync)
        {
            try
            {
                _app ??= new Zivid.NET.Application();
                if (_camera is null)
                {
                    progress?.Invoke("Connecting to camera...");
                    _camera = _app.ConnectCamera();
                }

                progress?.Invoke("Capturing calibration board...");
                Zivid.NET.Frame frame;
                try
                {
                    frame = Zivid.NET.Calibration.Detector.CaptureCalibrationBoard(_camera);
                }
                catch
                {
                    ResetConnection();
                    _camera = _app.ConnectCamera();
                    frame = Zivid.NET.Calibration.Detector.CaptureCalibrationBoard(_camera);
                }

                progress?.Invoke("Detecting board...");
                Zivid.NET.Calibration.DetectionResult detection;
                using (frame)
                    detection = Zivid.NET.Calibration.Detector.DetectCalibrationBoard(frame);

                bool valid = detection.Valid();
                string statusDesc = valid
                    ? $"Pose {_calibPoses.Count + 1} captured — board detected"
                    : $"Board not detected: {detection.StatusDescription()}";

                if (valid)
                    _calibPoses.Add((flangeInBase, detection));
                else
                    detection.Dispose();

                return new BoardDetectionResult { Detected = valid, Status = statusDesc };
            }
            catch (Exception ex)
            {
                ResetConnection();
                return new BoardDetectionResult { Detected = false, Status = $"Camera error: {ex.Message}" };
            }
        }
    }

    /// <summary>
    /// Runs the Zivid eye-in-hand calibration using all poses collected by
    /// <see cref="AddCalibrationPose"/>. Returns the camera-to-flange transform
    /// expressed as KUKA TCP X/Y/Z (mm) and A/B/C (°, ZYX Euler).
    /// </summary>
    public static HandEyeCalibResult RunHandEyeCalibration()
    {
        lock (Sync)
        {
            if (_calibPoses.Count < 3)
                return new HandEyeCalibResult
                {
                    Success = false,
                    Error   = $"Need at least 3 valid poses (have {_calibPoses.Count}).",
                };

            List<Zivid.NET.Calibration.HandEyeInput>? inputs = null;
            try
            {
                // Transpose each FK matrix: row-vector → column-vector for Zivid Pose.
                inputs = _calibPoses.Select(p =>
                {
                    var m   = Matrix4x4.Transpose(p.FlangeInBase);
                    var arr = new float[4, 4]
                    {
                        { m.M11, m.M12, m.M13, m.M14 },
                        { m.M21, m.M22, m.M23, m.M24 },
                        { m.M31, m.M32, m.M33, m.M34 },
                        { m.M41, m.M42, m.M43, m.M44 },
                    };
                    return new Zivid.NET.Calibration.HandEyeInput(
                        new Zivid.NET.Calibration.Pose(arr),
                        p.Detection);
                }).ToList();

                using var output = Zivid.NET.Calibration.Calibrator.CalibrateEyeInHand(inputs);

                if (!output.Valid())
                    return new HandEyeCalibResult { Success = false, Error = "Calibration returned an invalid result." };

                // Zivid eye-in-hand returns T_EE_camera — the camera pose in the robot
                // flange frame — in column-vector form. That IS the KUKA TCP directly,
                // PROVIDED the flange poses fed via AddCalibrationPose were expressed in
                // the same flange frame the viewport registers scans in (they are: the
                // caller passes the rendered-flange pose, not the analytic FK). So no
                // inversion is needed here.
                // Row i, Col j → T[i,j]. Translation column = camera origin in flange = TCP XYZ.
                var T  = output.Transform();
                float tx = T[0, 3], ty = T[1, 3], tz = T[2, 3];

                // Convert the column-vector rotation to row-vector form for MatrixToAbc:
                // row-vector R^T[i,j] = column-vector R[j,i] = T[j,i] (columns → rows).
                var rotRowVec = new Matrix4x4(
                    T[0, 0], T[1, 0], T[2, 0], 0,
                    T[0, 1], T[1, 1], T[2, 1], 0,
                    T[0, 2], T[1, 2], T[2, 2], 0,
                    0,       0,       0,       1);
                var (a, b, c) = KukaIkSolver.MatrixToAbc(rotRowVec);

                var residuals = output.Residuals();
                float avgRot   = residuals.Length > 0 ? residuals.Average(r => r.Rotation())    : 0f;
                float avgTrans = residuals.Length > 0 ? residuals.Average(r => r.Translation()) : 0f;

                return new HandEyeCalibResult
                {
                    Success             = true,
                    TcpX = tx, TcpY = ty, TcpZ = tz,
                    TcpA = a,  TcpB = b,  TcpC = c,
                    AvgRotResidualDeg   = avgRot,
                    AvgTransResidualMm  = avgTrans,
                };
            }
            catch (Exception ex)
            {
                return new HandEyeCalibResult { Success = false, Error = ex.Message };
            }
            finally
            {
                if (inputs is not null)
                    foreach (var inp in inputs)
                        try { inp.Dispose(); } catch { }
            }
        }
    }
}
