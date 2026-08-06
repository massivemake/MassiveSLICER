using System.Globalization;
using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Builds a standalone <c>.mass</c> workspace for material purge calibration:
/// small mesh + toolpath at home XY / 1 ft Z, settings for motor % and purge wait.
/// Open in a new MassiveSLICER instance; export to robot as usual after adjustments.
/// </summary>
public static class MaterialCalibrationWorkspace
{
    public const float DefaultPurgeHeightMm = MaterialCalibrationKrl.DefaultPurgeHeightMm;

    public sealed class CreateRequest
    {
        public required string SavePath { get; init; }
        public required MaterialPreset Material { get; init; }
        public float MotorPercent { get; init; } = 50f;
        public float RunTimeSec { get; init; } = 60f;
        public string? CellPath { get; init; }
        public CellConfig? Cell { get; init; }
        public float[]? HomeAngles { get; init; }
        public float HomeE1Mm { get; init; }
        public int ToolDataIndex { get; init; } = 1;
        public int BaseDataIndex { get; init; } = 1;
        public float PurgeHeightMm { get; init; } = DefaultPurgeHeightMm;
        public float ApproachZMm { get; init; } = 50f;
        /// <summary>Optional snapshot of live app prefs (theme, etc.); calib fields are overwritten.</summary>
        public AppPreferences? BasePreferences { get; init; }
    }

    /// <summary>
    /// Writes <paramref name="request"/>.SavePath and a small STL under workspace_meshes/.
    /// Returns the absolute path written.
    /// </summary>
    public static string Create(CreateRequest request)
    {
        string savePath = Path.GetFullPath(request.SavePath);
        if (!savePath.EndsWith(".mass", StringComparison.OrdinalIgnoreCase))
            savePath += ".mass";

        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        string meshDir = WorkspaceLoader.MeshesDirFor(savePath);
        Directory.CreateDirectory(meshDir);

        float motor = Math.Clamp(request.MotorPercent, 0.1f, 100f);
        float time  = Math.Max(1f, request.RunTimeSec);
        float zPurge = Math.Max(1f, request.PurgeHeightMm);

        float[] home = request.HomeAngles is { Length: >= 6 } h
            ? h
            : request.Cell?.Robot.HomePosition is { Length: >= 6 } ch
                ? ch
                : [0f, -90f, 90f, 0f, 15f, 0f];
        float e1 = float.IsNaN(request.HomeE1Mm) ? 0f : request.HomeE1Mm;

        // Home TCP in BASE, then store toolpath points in world (export converts back).
        MaterialCalibrationKrl.CartesianPose homeTcp;
        if (request.Cell is { } cell)
            homeTcp = MaterialCalibrationKrl.EstimateHomeTcpInBase(home, e1, cell);
        else
            homeTcp = new MaterialCalibrationKrl.CartesianPose(0, 0, zPurge + 200f, 0, 90, 0);

        var rob = request.Cell?.Robot.WorldPosition ?? new Float3(0, 0, 0);
        var bas = request.Cell?.Bed.BaseData ?? new Float3(0, 0, 0);

        // World coords for purge XY at 1 ft Z
        float wx = rob.X + bas.X + homeTcp.X;
        float wy = rob.Y + bas.Y + homeTcp.Y;
        float wzPurge = rob.Z + bas.Z + zPurge;
        // Short extrude segment so the exporter emits RPM-on + wait then a LIN
        float seg = 5f; // mm

        var purgeFrom = new Vector3(wx, wy, wzPurge);
        var purgeTo   = new Vector3(wx + seg, wy, wzPurge);

        var toolpath = new Toolpath();
        var layer = new ToolpathLayer(0, zPurge)
        {
            Height      = 3f,
            PlaneNormal = Vector3.UnitZ,
        };
        layer.Moves.Add(new ToolpathMove(purgeFrom, purgeTo, MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        layer.Contours.Add(new ContourSpan(0, 1, Closed: false, EntryTravelIndex: -1));
        toolpath.Layers.Add(layer);

        string meshFile = $"{Guid.NewGuid():N}.stl";
        string meshPath = Path.Combine(meshDir, meshFile);
        WriteBoxStl(meshPath, purgeFrom, sizeMm: 20f);

        var tpEntry = new WorkspaceToolpathEntry
        {
            Name          = "Material Calibration Purge",
            Visible       = true,
            BeadWidth     = 0.01f,
            LayerHeight   = 0.01f,
            MaterialColor = [0.2f, 0.75f, 0.35f],
            Data          = ToolpathSerializer.ToData(toolpath),
            RawData       = ToolpathSerializer.ToData(toolpath),
        };

        var model = new WorkspaceModelEntry
        {
            Name             = "Material Calibration",
            Visible          = true,
            EmbeddedMeshPath = WorkspaceLoader.ToRelativeMeshPath(meshFile),
            LocalTransform   = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
            Toolpaths        = [tpEntry],
        };

        var prefs = ClonePrefs(request.BasePreferences);
        prefs.SelectedMaterialPresetName = request.Material.Name;
        // Near-zero geometry → computed RPM% ≈ 0; offset forces the motor % for export.
        prefs.BeadWidth   = 0.01;
        prefs.LayerHeight = 0.01;
        prefs.FirstLayerHeight = 0.01;
        prefs.PrintSpeed  = 50;
        prefs.TravelSpeed = 80;
        prefs.ApproachZ   = request.ApproachZMm;
        // Force the screw speed through a dedicated calibration override, NOT
        // ExtrusionSpeedOffset — that field is used on real jobs, and leaving a calibration
        // value in it silently inflated the flow of everything sliced afterwards.
        prefs.ExtrusionRpmOverridePercent = motor;
        prefs.ExtrusionSpeedOffset = "";
        prefs.ExtrusionStartWaitSec = time;
        prefs.ExtrusionResumeWaitSec = 0;
        prefs.WipeModeDisplay = "Off";
        prefs.ZHopMm = 0;
        prefs.ToolDataIndex = request.ToolDataIndex;
        prefs.BaseDataIndex = request.BaseDataIndex;
        prefs.TemperatureOffset = "";
        // Zone temps come from the selected material preset at export (GetEffectiveExportTemperature).
        // Store nothing special here beyond material name.

        var doc = new WorkspaceDocument
        {
            Version       = 2,
            CellPath      = WorkspaceCellPath.NormalizeForSave(request.CellPath),
            RightPanelTab = "Additive",
            Settings      = prefs,
            Models        = [model],
            Camera        = new CameraView
            {
                Azimuth   = -45,
                Elevation = 25,
                Radius    = 2500,
                TargetX   = wx,
                TargetY   = wy,
                TargetZ   = wzPurge,
            },
            UiSession = new WorkspaceUiSession
            {
                ViewMode             = "Toolpath",
                IsScrubSessionActive = true,
                SelectToolpath       = true,
                ScrubModelName       = model.Name,
                ScrubToolpathName    = tpEntry.Name,
                ToolpathScrubIndex   = 1,
                ToolpathScrubLowIndex = 0,
                ToolpathScrubLayerHigh = 1,
                ToolpathScrubLayerLow  = 1,
                RobotJoints = [home[0], home[1], home[2], home[3], home[4], home[5], e1],
            },
        };

        WorkspaceLoader.Save(doc, savePath);
        return savePath;
    }

    /// <summary>Default save path under AppData for calibration workspaces.</summary>
    public static string SuggestSavePath(string materialName)
    {
        string dir = Path.Combine(WorkspaceLoader.WorkspaceDir, "MaterialCalibration");
        Directory.CreateDirectory(dir);
        string stem = MaterialCalibrationKrl.SuggestProgramName(materialName);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(dir, $"{stem}_{stamp}.mass");
    }

    private static AppPreferences ClonePrefs(AppPreferences? src)
    {
        if (src is null) return new AppPreferences();
        try
        {
            string json = System.Text.Json.JsonSerializer.Serialize(src);
            return System.Text.Json.JsonSerializer.Deserialize<AppPreferences>(json) ?? new AppPreferences();
        }
        catch
        {
            return new AppPreferences();
        }
    }

    /// <summary>Minimal binary STL box centered near the purge point (for outliner / export parent).</summary>
    private static void WriteBoxStl(string path, Vector3 center, float sizeMm)
    {
        float h = sizeMm * 0.5f;
        // 8 corners
        var c = new Vector3[8];
        int n = 0;
        for (int dz = 0; dz < 2; dz++)
        for (int dy = 0; dy < 2; dy++)
        for (int dx = 0; dx < 2; dx++)
            c[n++] = center + new Vector3((dx * 2 - 1) * h, (dy * 2 - 1) * h, (dz * 2 - 1) * h);

        // 12 triangles (2 per face)
        int[][] faces =
        [
            [0, 2, 3, 0, 3, 1], // -Z? use consistent winding
            [4, 5, 7, 4, 7, 6],
            [0, 1, 5, 0, 5, 4],
            [2, 6, 7, 2, 7, 3],
            [0, 4, 6, 0, 6, 2],
            [1, 3, 7, 1, 7, 5],
        ];

        using var fs = File.Create(path);
        using var w  = new BinaryWriter(fs);
        w.Write(new byte[80]);
        w.Write((uint)(faces.Length * 2));
        foreach (var f in faces)
        {
            WriteTri(w, c[f[0]], c[f[1]], c[f[2]]);
            WriteTri(w, c[f[3]], c[f[4]], c[f[5]]);
        }
    }

    private static void WriteTri(BinaryWriter w, Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (n.LengthSquared() < 1e-12f) n = Vector3.UnitZ;
        WriteV(w, n); WriteV(w, a); WriteV(w, b); WriteV(w, c);
        w.Write((ushort)0);
    }

    private static void WriteV(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
    }
}
