using System.Globalization;
using System.Numerics;
using System.Text;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Builds a standalone <c>.mass</c> workspace for Start &amp; Stop (travel) calibration.
/// Default layout: a <b>2×4 grid of 8 dual-wall test cells</b>, each with different
/// wipe / z-hop settings baked into one sequential toolpath (one-shot series print).
/// Set <see cref="CreateRequest.CircleDiameterMm"/> &gt; 0 for single-wall circle rings
/// (e.g. 8 in = 203.2 mm on LFAM 2) with the same T1…T8 DOE matrix.
/// <para>
/// Project name: <b>Start and Stop Calibration Effort V1</b> (walls) or
/// <b>Start and Stop Calibration LFAM2 Circles 8in</b> (circles helper).
/// </para>
/// </summary>
public static class StartStopCalibrationWorkspace
{
    public const string ProjectName = "Start and Stop Calibration Effort V1";
    public const string ProjectSlug = "Start_and_Stop_Calibration_Effort_V1";

    /// <summary>LFAM 2 circle series (8 in diameter rings, same T1…T8 matrix).</summary>
    public const string CirclesLfam2ProjectName = "Start and Stop Calibration LFAM2 Circles 8in";
    public const string CirclesLfam2ProjectSlug = "Start_and_Stop_Calibration_LFAM2_Circles_8in";

    /// <summary>8 inch outer diameter in millimetres.</summary>
    public const float EightInchDiameterMm = 8f * 25.4f; // 203.2

    /// <summary>Caracol-suggested travel / wipe speed (mm/s).</summary>
    public const float DefaultTravelMmS = 600f;

    /// <summary>Caracol Eidos demo print speed (mm/s).</summary>
    public const float DefaultPrintMmS = 30f;

    /// <summary>V1 shop target layer height (mm).</summary>
    public const float DefaultLayerHeightMm = 3f;

    /// <summary>V1 shop target bead width (mm).</summary>
    public const float DefaultBeadWidthMm = 6.5f;
    public const float DefaultZHopMm = 3f;
    public const float DefaultWipeLengthMm = 8f;
    public const float DefaultResumeWaitSec = 0.5f;
    public const float DefaultStartWaitSec = 2f;
    public const int DefaultCircleSegments = 48;

    /// <summary>
    /// Recommended production S&amp;S package (Caracol mid-target / former T5):
    /// same-dir wipe 12 mm, ramp 4, z-hop 3, resume 500 ms.
    /// </summary>
    public static TestCell RecommendedSsCell(int id, string code) =>
        new(id, code, "RECOMMENDED · Wipe12 · hop3 · resume 500ms",
            WipeMode.SameDirection, 12f, 4f, 3f, 500);

    /// <summary>Default 8-cell DOE matrix (order = print order = T1…T8). ResumeWaitMs is per-cell.</summary>
    public static IReadOnlyList<TestCell> DefaultTestMatrix { get; } =
    [
        //                        wipe                    len  ramp hop  resumeMs
        new(1, "T1", "No wipe · hop0 · resume 0ms",     WipeMode.None,           0f,  0f, 0f,   0),
        new(2, "T2", "No wipe · hop3 · resume 500ms",   WipeMode.None,           0f,  0f, 3f, 500),
        new(3, "T3", "Wipe6 · hop3 · resume 0ms",       WipeMode.SameDirection,  6f,  3f, 3f,   0),
        new(4, "T4", "Wipe12 · hop3 · resume 250ms",    WipeMode.SameDirection, 12f,  4f, 3f, 250),
        // T5 + T6: two identical recommended lines (repeatability check).
        RecommendedSsCell(5, "T5"),
        RecommendedSsCell(6, "T6"),
        new(7, "T7", "Retrace12 · hop3 · resume 500ms", WipeMode.Retrace,       12f,  4f, 3f, 500),
        new(8, "T8", "Wipe12 · hop6 · resume 500ms",    WipeMode.SameDirection, 12f,  4f, 6f, 500),
    ];

    /// <summary>One dual-wall site in the calibration grid.</summary>
    public sealed record TestCell(
        int Id,
        string Code,
        string Label,
        WipeMode WipeMode,
        float WipeLengthMm,
        float WipeRampMm,
        float ZHopMm,
        /// <summary>Resume pause after each travel in this cell, in milliseconds.</summary>
        int ResumeWaitMs);

    public sealed class CreateRequest
    {
        public required string SavePath { get; init; }
        public string? CellPath { get; init; }
        public CellConfig? Cell { get; init; }
        public float[]? HomeAngles { get; init; }
        public float HomeE1Mm { get; init; }
        public int ToolDataIndex { get; init; } = 1;
        public int BaseDataIndex { get; init; } = 1;
        public AppPreferences? BasePreferences { get; init; }
        public string? MaterialName { get; init; }

        /// <summary>Wall length along Y (mm) for each dual-wall pair.</summary>
        public float WallLengthMm { get; init; } = 50f;

        /// <summary>Wall thickness along X (mm). Slightly above bead width.</summary>
        public float WallThicknessMm { get; init; } = 9f;

        /// <summary>Wall height (mm). Short for a full 8-cell series (~8 layers @ 3 mm).</summary>
        public float WallHeightMm { get; init; } = 24f;

        /// <summary>Clear gap between the two walls of one cell (mm).</summary>
        public float GapMm { get; init; } = 40f;

        /// <summary>Centre of T1 (bottom-left of grid) in BASE XY mm.</summary>
        public float BaseXMm { get; init; } = 1500f;

        /// <summary>See <see cref="BaseXMm"/>.</summary>
        public float BaseYMm { get; init; } = 600f;

        /// <summary>Grid pitch in BASE X (mm) between cell centres.</summary>
        public float PitchXMm { get; init; } = 130f;

        /// <summary>Grid pitch in BASE Y (mm) between cell centres.</summary>
        public float PitchYMm { get; init; } = 150f;

        /// <summary>Columns in the grid (default 4 → 2 rows for 8 cells).</summary>
        public int GridColumns { get; init; } = 4;

        public float BeadWidthMm { get; init; } = DefaultBeadWidthMm;
        public float LayerHeightMm { get; init; } = DefaultLayerHeightMm;
        public float PrintSpeedMmS { get; init; } = DefaultPrintMmS;
        public float TravelSpeedMmS { get; init; } = DefaultTravelMmS;
        public float WipeSpeedMmS { get; init; } = DefaultTravelMmS;
        public float ExtrusionStartWaitSec { get; init; } = DefaultStartWaitSec;
        public float ExtrusionResumeWaitSec { get; init; } = DefaultResumeWaitSec;
        public float ApproachZMm { get; init; } = 50f;

        /// <summary>When true, embeds the baked multi-cell toolpath (required for one-shot series).</summary>
        public bool IncludeBaselineToolpath { get; init; } = true;

        /// <summary>Override the default 8-cell matrix (must be non-empty when set).</summary>
        public IReadOnlyList<TestCell>? Tests { get; init; }

        /// <summary>
        /// When &gt; 0, each cell is a single closed circle ring of this outer diameter (mm)
        /// instead of a dual-wall pair. Use <see cref="EightInchDiameterMm"/> for 8 in.
        /// </summary>
        public float CircleDiameterMm { get; init; }

        /// <summary>Polyline segments for circle rings (toolpath + mesh).</summary>
        public int CircleSegments { get; init; } = DefaultCircleSegments;

        /// <summary>Optional display / legend title override.</summary>
        public string? ProjectDisplayName { get; init; }
    }

    /// <summary>
    /// Writes the grid workspace + mesh + baked toolpath + LEGEND.txt sidecar.
    /// Dual-wall boxes by default; circle rings when <see cref="CreateRequest.CircleDiameterMm"/> &gt; 0.
    /// </summary>
    public static string Create(CreateRequest request)
    {
        string savePath = Path.GetFullPath(request.SavePath);
        if (!savePath.EndsWith(".mass", StringComparison.OrdinalIgnoreCase))
            savePath += ".mass";

        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        string meshDir = WorkspaceLoader.MeshesDirFor(savePath);
        Directory.CreateDirectory(meshDir);

        float[] home = request.HomeAngles is { Length: >= 6 } h
            ? h
            : request.Cell?.Robot.HomePosition is { Length: >= 6 } ch
                ? ch
                : [0f, -90f, 90f, 0f, 15f, 0f];
        float e1 = float.IsNaN(request.HomeE1Mm) ? 0f : request.HomeE1Mm;

        var rob = request.Cell?.Robot.WorldPosition ?? new Float3(0, 0, 0);
        float originX, originY, bedZ;
        if (request.Cell is { } cell)
        {
            var baseMarker = cell.Bed.BaseMarkerWorld(rob);
            originX = baseMarker.X + request.BaseXMm;
            originY = baseMarker.Y + request.BaseYMm;
            bedZ    = baseMarker.Z;
        }
        else
        {
            originX = request.BaseXMm;
            originY = request.BaseYMm;
            bedZ    = 0f;
        }

        var tests = request.Tests is { Count: > 0 } t ? t : DefaultTestMatrix;
        int cols = Math.Max(1, request.GridColumns);
        int rows = (tests.Count + cols - 1) / cols;

        bool useCircles = request.CircleDiameterMm > 1f;
        float diameter = Math.Max(20f, request.CircleDiameterMm);
        float radius = diameter * 0.5f;
        int segments = Math.Clamp(request.CircleSegments, 12, 256);

        float L = Math.Max(20f, request.WallLengthMm);
        float T = Math.Max(4f, request.WallThicknessMm);
        float H = Math.Max(request.LayerHeightMm * 2f, request.WallHeightMm);
        // Dual 8" rings per cell: clear gap between outer walls, then pitch between cells.
        float G = useCircles
            ? Math.Max(40f, request.GapMm)
            : Math.Max(10f, request.GapMm);
        float halfSpan = useCircles
            ? radius + G * 0.5f
            : (T + G) * 0.5f;
        float pitchX = useCircles
            ? Math.Max(diameter * 2f + G + 80f, request.PitchXMm)
            : Math.Max(T + G + 40f, request.PitchXMm);
        float pitchY = useCircles
            ? Math.Max(diameter + 80f, request.PitchYMm)
            : Math.Max(L + 40f, request.PitchYMm);

        float layerH = Math.Max(0.5f, request.LayerHeightMm);
        float beadW  = Math.Max(1f, request.BeadWidthMm);
        int layerCount = Math.Max(2, (int)MathF.Round(H / layerH));
        float travelMps = request.TravelSpeedMmS / 1000f;
        float printMps  = request.PrintSpeedMmS / 1000f;
        float wipeMps   = request.WipeSpeedMmS / 1000f;

        // Place each cell: col = (i % cols), row = (i / cols); T1 at bottom-left.
        // WallA/WallB unused for circles (centre stored in Cx/Cy).
        var placements = new List<(TestCell Test, Vector3 WallA, Vector3 WallB, float Cx, float Cy)>(tests.Count);
        for (int i = 0; i < tests.Count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            float cx = originX + col * pitchX;
            float cy = originY + row * pitchY;
            // WallA / WallB = left/right island centres (dual walls or dual circles).
            var wallA = new Vector3(cx - halfSpan, cy, bedZ + H * 0.5f);
            var wallB = new Vector3(cx + halfSpan, cy, bedZ + H * 0.5f);
            placements.Add((tests[i], wallA, wallB, cx, cy));
        }

        string meshFile = $"{Guid.NewGuid():N}.stl";
        string meshPath = Path.Combine(meshDir, meshFile);
        if (useCircles)
            WriteCircleGridStl(meshPath, placements, radius, beadW, H, segments);
        else
            WriteGridStl(meshPath, placements, T, L, H);

        Toolpath? toolpath = null;
        if (request.IncludeBaselineToolpath)
        {
            toolpath = useCircles
                ? BuildCircleGridToolpath(
                    placements, radius, beadW, bedZ, layerH, layerCount, segments,
                    printMps, travelMps, wipeMps)
                : BuildGridToolpath(
                    placements, T, L, bedZ, layerH, layerCount, beadW,
                    printMps, travelMps, wipeMps);
        }

        string displayName = !string.IsNullOrWhiteSpace(request.ProjectDisplayName)
            ? request.ProjectDisplayName!
            : useCircles
                ? CirclesLfam2ProjectName
                : ProjectName;
        string geomLabel = useCircles
            ? $"8-cell {diameter / 25.4f:0.#}in circles"
            : "8-cell grid";

        var model = new WorkspaceModelEntry
        {
            Name             = $"{displayName} — {geomLabel}",
            Visible          = true,
            EmbeddedMeshPath = WorkspaceLoader.ToRelativeMeshPath(meshFile),
            LocalTransform   = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
            Toolpaths        = [],
        };

        if (toolpath is not null)
        {
            model.Toolpaths.Add(new WorkspaceToolpathEntry
            {
                Name          = "S&S 8-cell matrix (BAKED — do not re-slice)",
                Visible       = true,
                BeadWidth     = beadW,
                LayerHeight   = layerH,
                MaterialColor = [0.95f, 0.45f, 0.12f],
                Data          = ToolpathSerializer.ToData(toolpath),
                RawData       = ToolpathSerializer.ToData(toolpath),
            });
        }

        var prefs = ClonePrefs(request.BasePreferences);
        if (!string.IsNullOrWhiteSpace(request.MaterialName))
            prefs.SelectedMaterialPresetName = request.MaterialName;

        // UI defaults = T4 mid-range; actual motion is baked per cell.
        prefs.BeadWidth              = beadW;
        prefs.LayerHeight            = layerH;
        prefs.FirstLayerHeight       = layerH;
        prefs.PrintSpeed             = request.PrintSpeedMmS;
        prefs.TravelSpeed            = request.TravelSpeedMmS;
        prefs.WipeSpeed              = request.WipeSpeedMmS;
        prefs.ApproachZ              = request.ApproachZMm;
        prefs.ZHopMm                 = 3;
        prefs.WipeModeDisplay        = "Same-Direction";
        prefs.WipeLengthMm           = 12;
        prefs.WipeRampMm             = 4;
        prefs.WipeSkipShortTravels   = true;
        prefs.ExtrusionStartWaitSec  = request.ExtrusionStartWaitSec;
        // Global fallback; per-cell resume is baked on travel moves (ResumeWaitSec).
        prefs.ExtrusionResumeWaitSec = request.ExtrusionResumeWaitSec;
        // Calibration series: Caracol URM body (MTruck travel start/end + RPM=).
        prefs.DigitalStartStopEnabled = true;
        prefs.InfillPattern          = "None";
        prefs.SliceMethod            = "Planar";
        prefs.SlicingMode            = "Normal";
        prefs.ToolDataIndex          = request.ToolDataIndex;
        prefs.BaseDataIndex          = request.BaseDataIndex;
        prefs.ResumeRampEnabled      = false;
        prefs.LayerSpeedAdaptEnabled = false;
        prefs.AdaptiveLayerHeight    = false;

        float gridW = useCircles
            ? (cols - 1) * pitchX + (diameter * 2f + G)
            : (cols - 1) * pitchX + (T + G + T);
        float gridD = useCircles
            ? (rows - 1) * pitchY + diameter
            : (rows - 1) * pitchY + L;
        float camX  = originX + (cols - 1) * pitchX * 0.5f;
        float camY  = originY + (rows - 1) * pitchY * 0.5f;

        int totalLayers = toolpath?.Layers.Count ?? layerCount;

        var doc = new WorkspaceDocument
        {
            Version       = 2,
            CellPath      = WorkspaceCellPath.NormalizeForSave(request.CellPath),
            RightPanelTab = "Additive",
            Settings      = prefs,
            Models        = [model],
            Camera        = new CameraView
            {
                Azimuth   = -35,
                Elevation = 40,
                Radius    = Math.Max(1200, MathF.Sqrt(gridW * gridW + gridD * gridD) * 1.4f + 400f),
                TargetX   = camX,
                TargetY   = camY,
                TargetZ   = bedZ + H * 0.5f,
            },
            UiSession = new WorkspaceUiSession
            {
                ViewMode             = toolpath is not null ? "Toolpath" : "Body",
                IsScrubSessionActive = toolpath is not null,
                SelectToolpath       = toolpath is not null,
                ScrubModelName       = model.Name,
                ScrubToolpathName    = toolpath is not null ? model.Toolpaths[0].Name : null,
                ToolpathScrubLayerHigh = totalLayers,
                ToolpathScrubLayerLow  = 1,
                // Keep the 8-cell BAKED matrix — do not re-slice on open/settings load.
                RealtimeSlicingPaused  = true,
                RobotJoints = [home[0], home[1], home[2], home[3], home[4], home[5], e1],
            },
        };

        WorkspaceLoader.Save(doc, savePath);

        string legendName = useCircles ? "TEST_MATRIX_CIRCLES_8IN.txt" : "TEST_MATRIX.txt";
        string legendPath = Path.Combine(Path.GetDirectoryName(savePath)!, legendName);
        File.WriteAllText(legendPath, FormatLegend(
            placements, cols, request.BaseXMm, request.BaseYMm, pitchX, pitchY,
            request.ExtrusionResumeWaitSec, request.ExtrusionStartWaitSec,
            layerCount, L, T, G, H, displayName, useCircles ? diameter : 0f));

        return savePath;
    }

    public static string SuggestSavePath(string? stamp = null)
    {
        string dir = Path.Combine(WorkspaceLoader.WorkspaceDir, "StartStopCalibration");
        Directory.CreateDirectory(dir);
        stamp ??= DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(dir, $"{ProjectSlug}_{stamp}.mass");
    }

    public static string ProjectWorkspacePath()
    {
        string dir = Path.Combine(WorkspaceLoader.WorkspaceDir, "StartStopCalibration");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{ProjectSlug}.mass");
    }

    /// <summary>Canonical path for the LFAM 2 / 8 in circle S&amp;S series.</summary>
    public static string CirclesLfam2ProjectWorkspacePath()
    {
        string dir = Path.Combine(WorkspaceLoader.WorkspaceDir, "StartStopCalibration");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{CirclesLfam2ProjectSlug}.mass");
    }

    public static string FormatLegend(
        IReadOnlyList<(TestCell Test, Vector3 WallA, Vector3 WallB, float Cx, float Cy)> placements,
        int cols,
        float baseX,
        float baseY,
        float pitchX,
        float pitchY,
        float resumeWait,
        float startWait,
        int layersPerCell,
        float wallLen,
        float wallThk,
        float gap,
        float height,
        string? projectDisplayName = null,
        float circleDiameterMm = 0f)
    {
        bool circles = circleDiameterMm > 1f;
        var sb = new StringBuilder();
        sb.AppendLine(projectDisplayName ?? ProjectName);
        sb.AppendLine(circles
            ? $"8-cell Start/Stop circle series  (Ø {circleDiameterMm:0.#} mm / {circleDiameterMm / 25.4f:0.##} in)"
            : "8-cell Start/Stop one-shot matrix");
        sb.AppendLine(new string('=', 64));
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Toolpath is BAKED per cell. Do NOT re-slice — that would");
        sb.AppendLine("apply one global wipe/z-hop to every cell and erase the matrix.");
        sb.AppendLine();
        sb.AppendLine($"Grid origin (T1 centre) BASE XY: ({baseX:0}, {baseY:0}) mm");
        sb.AppendLine($"Pitch: X={pitchX:0} mm  Y={pitchY:0} mm   Columns={cols}");
        if (circles)
            sb.AppendLine($"Cell geometry: dual circles Ø {circleDiameterMm:0.#} mm × {height:0} mm, gap {gap:0} mm between rings");
        else
            sb.AppendLine($"Cell geometry: walls {wallThk:0}×{wallLen:0}×{height:0} mm, gap {gap:0} mm");
        sb.AppendLine($"Layers/cell: {layersPerCell}   Print order: T1 → T2 → … → T8 (serial towers)");
        sb.AppendLine($"Global fallback ResumeWait={resumeWait:0.###}s  StartWait={startWait:0.###}s");
        sb.AppendLine("  Per-cell resume wait is baked on travels (see ResumeMs column).");
        sb.AppendLine("  Digital Start/Stop (URM) is ON for this workspace (PRINT TOOLPATH checkbox).");
        sb.AppendLine();
        sb.AppendLine("Layout (top view, +Y up / +X right):");
        sb.AppendLine();

        int rows = (placements.Count + cols - 1) / cols;
        for (int r = rows - 1; r >= 0; r--)
        {
            var parts = new List<string>();
            for (int c = 0; c < cols; c++)
            {
                int i = r * cols + c;
                if (i < placements.Count)
                    parts.Add($"{placements[i].Test.Code,-4}");
            }
            sb.AppendLine("  " + string.Join("  ", parts));
        }

        sb.AppendLine();
        sb.AppendLine("Matrix:");
        sb.AppendLine($"  {"#",-4} {"Code",-4} {"Wipe",-12} {"Len",-5} {"Ramp",-5} {"ZHop",-5} {"ResumeMs",-9} Label");
        sb.AppendLine("  " + new string('-', 78));
        foreach (var (test, _, _, _, _) in placements)
        {
            string wipe = test.WipeMode switch
            {
                WipeMode.None => "Off",
                WipeMode.Retrace => "Retrace",
                WipeMode.SameDirection => "Same-Dir",
                _ => test.WipeMode.ToString(),
            };
            sb.AppendLine(
                $"  {test.Id,-4} {test.Code,-4} {wipe,-12} {test.WipeLengthMm,-5:0} {test.WipeRampMm,-5:0} {test.ZHopMm,-5:0} {test.ResumeWaitMs,-9} {test.Label}");
        }

        sb.AppendLine();
        if (circles)
        {
            sb.AppendLine("Score each pair at the stop seam (end of circle A) and start seam (begin of circle B).");
            sb.AppendLine("Also score inter-cell connectors (T1→T2 …). Pick the cleanest cell for production defaults.");
        }
        else
        {
            sb.AppendLine("Score each pair at the stop seam (end of wall A) and start seam (begin of wall B).");
            sb.AppendLine("Circle the cleanest cell → that becomes production defaults.");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Serial towers: fully print cell N (all layers, A↔B travels with that cell's wipe/zhop),
    /// then z-hop travel to cell N+1.
    /// </summary>
    private static Toolpath BuildGridToolpath(
        IReadOnlyList<(TestCell Test, Vector3 WallA, Vector3 WallB, float Cx, float Cy)> placements,
        float thicknessMm,
        float lengthMm,
        float bedZ,
        float layerH,
        int layerCount,
        float beadWidth,
        float printMps,
        float travelMps,
        float wipeMps)
        => AssembleSerialMatrix(
            placements,
            (test, wallA, wallB, _, _) => BuildDualIslandToolpath(
                wallA, wallB, thicknessMm, lengthMm, bedZ, layerH, layerCount, beadWidth),
            layerH, beadWidth, printMps, travelMps, wipeMps);

    /// <summary>
    /// Same serial matrix as dual-wall: each T-cell has <b>two</b>  circle rings (A/B)
    /// side-by-side so mid-cell travel carries wipe / z-hop / resume under test.
    /// </summary>
    private static Toolpath BuildCircleGridToolpath(
        IReadOnlyList<(TestCell Test, Vector3 WallA, Vector3 WallB, float Cx, float Cy)> placements,
        float radiusMm,
        float beadWidth,
        float bedZ,
        float layerH,
        int layerCount,
        int segments,
        float printMps,
        float travelMps,
        float wipeMps)
    {
        // Toolpath radius on bead centreline (outer mesh radius − half bead).
        float pathR = Math.Max(5f, radiusMm - beadWidth * 0.5f);
        // Centres of the dual rings: same pitch as mesh (WallA / WallB already offset in placements).
        return AssembleSerialMatrix(
            placements,
            (_, wallA, wallB, _, _) => BuildDualCircleToolpath(
                wallA.X, wallA.Y, wallB.X, wallB.Y, pathR, bedZ, layerH, layerCount, segments),
            layerH, beadWidth, printMps, travelMps, wipeMps);
    }

    private static Toolpath AssembleSerialMatrix(
        IReadOnlyList<(TestCell Test, Vector3 WallA, Vector3 WallB, float Cx, float Cy)> placements,
        Func<TestCell, Vector3, Vector3, float, float, Toolpath> buildCell,
        float layerH,
        float beadWidth,
        float printMps,
        float travelMps,
        float wipeMps)
    {
        var result = new Toolpath();
        int layerIndex = 0;
        Vector3? lastEnd = null;

        foreach (var (test, wallA, wallB, cx, cy) in placements)
        {
            var cellPath = buildCell(test, wallA, wallB, cx, cy);

            var settings = new SliceSettings
            {
                LayerHeight          = layerH,
                BeadWidth            = beadWidth,
                PrintSpeedMps        = printMps,
                TravelSpeed          = travelMps,
                WipeSpeed            = wipeMps,
                ZHopMm               = test.ZHopMm,
                WipeMode             = test.WipeMode,
                WipeLengthMm         = test.WipeLengthMm,
                WipeRampMm           = test.WipeRampMm,
                WipeSkipShortTravels = true,
            };
            cellPath = MovementPostProcessor.Apply(cellPath, settings);
            float resumeSec = Math.Max(0f, test.ResumeWaitMs / 1000f);
            cellPath = TagResumeWait(cellPath, resumeSec);

            for (int li = 0; li < cellPath.Layers.Count; li++)
            {
                var src = cellPath.Layers[li];
                var layer = new ToolpathLayer(layerIndex++, src.Z)
                {
                    Height      = src.Height,
                    PlaneNormal = src.PlaneNormal,
                };

                if (lastEnd is { } prev && src.Moves.Count > 0)
                {
                    var nextStart = src.Moves[0].From;
                    float hop = Math.Max(test.ZHopMm, 5f);
                    foreach (var hopMove in BuildConnector(prev, nextStart, hop, travelMps))
                        layer.Moves.Add(hopMove with { ResumeWaitSec = resumeSec });
                }

                int moveOffset = layer.Moves.Count;
                foreach (var m in src.Moves)
                    layer.Moves.Add(m);

                foreach (var c in src.Contours)
                {
                    int entry = c.EntryTravelIndex < 0 ? -1 : c.EntryTravelIndex + moveOffset;
                    layer.Contours.Add(new ContourSpan(
                        c.Start + moveOffset, c.Count, c.Closed, entry));
                }

                if (layer.Moves.Count > 0)
                    lastEnd = layer.Moves[^1].To;

                result.Layers.Add(layer);
            }
        }

        return result;
    }

    /// <summary>
    /// Dual closed circle rings per layer (centres at A/B) — same Start/Stop travel pattern
    /// as dual-wall cells so wipe / z-hop / resume apply on the A→B hop.
    /// </summary>
    private static Toolpath BuildDualCircleToolpath(
        float ax, float ay,
        float bx, float by,
        float pathRadiusMm,
        float bedZ,
        float layerH,
        int layerCount,
        int segments)
    {
        var toolpath = new Toolpath();
        Vector3? last = null;
        int segs = Math.Max(16, segments);

        for (int li = 0; li < layerCount; li++)
        {
            float z = bedZ + layerH * (li + 1);
            var layer = new ToolpathLayer(li, z)
            {
                Height      = layerH,
                PlaneNormal = Vector3.UnitZ,
            };

            bool aFirst = li % 2 == 0;
            var firstLoop  = CircleLoop(aFirst ? ax : bx, aFirst ? ay : by, pathRadiusMm, z, segs);
            var secondLoop = CircleLoop(aFirst ? bx : ax, aFirst ? by : ay, pathRadiusMm, z, segs);

            if (last is { } prev)
            {
                int travelIdx = layer.Moves.Count;
                layer.Moves.Add(new ToolpathMove(prev, firstLoop[0], MoveKind.Travel)
                {
                    Normal = Vector3.UnitZ,
                });
                AppendClosedLoop(layer, firstLoop, travelIdx);
            }
            else
            {
                AppendClosedLoop(layer, firstLoop, entryTravelIndex: -1);
            }

            {
                int travelIdx = layer.Moves.Count;
                layer.Moves.Add(new ToolpathMove(firstLoop[^1], secondLoop[0], MoveKind.Travel)
                {
                    Normal = Vector3.UnitZ,
                });
                AppendClosedLoop(layer, secondLoop, travelIdx);
            }

            last = secondLoop[^1];
            toolpath.Layers.Add(layer);
        }

        return toolpath;
    }

    private static Vector3[] CircleLoop(float cx, float cy, float radius, float z, int segments)
    {
        var pts = new Vector3[segments + 1];
        for (int i = 0; i < segments; i++)
        {
            float a = i * (MathF.PI * 2f / segments);
            pts[i] = new Vector3(cx + radius * MathF.Cos(a), cy + radius * MathF.Sin(a), z);
        }
        pts[segments] = pts[0];
        return pts;
    }

    private static Toolpath TagResumeWait(Toolpath tp, float resumeSec)
    {
        var result = new Toolpath();
        foreach (var layer in tp.Layers)
        {
            var newLayer = new ToolpathLayer(layer.Index, layer.Z)
            {
                Height      = layer.Height,
                PlaneNormal = layer.PlaneNormal,
            };
            newLayer.Contours.AddRange(layer.Contours);
            foreach (var m in layer.Moves)
            {
                if (m.Kind == MoveKind.Travel || m.IsWipe)
                    newLayer.Moves.Add(m with { ResumeWaitSec = resumeSec });
                else
                    newLayer.Moves.Add(m);
            }
            result.Layers.Add(newLayer);
        }
        return result;
    }

    private static IEnumerable<ToolpathMove> BuildConnector(
        Vector3 from, Vector3 to, float zHop, float travelMps)
    {
        float topZ = Math.Max(from.Z, to.Z) + zHop;
        var up   = new Vector3(from.X, from.Y, topZ);
        var over = new Vector3(to.X, to.Y, topZ);

        yield return new ToolpathMove(from, up, MoveKind.Travel)
        {
            IsZHop = true, IsMergeConnector = true, TravelSpeedMps = travelMps, Normal = Vector3.UnitZ,
        };
        yield return new ToolpathMove(up, over, MoveKind.Travel)
        {
            IsZHop = true, IsMergeConnector = true, TravelSpeedMps = travelMps, Normal = Vector3.UnitZ,
        };
        yield return new ToolpathMove(over, to, MoveKind.Travel)
        {
            IsZHop = true, IsMergeConnector = true, TravelSpeedMps = travelMps, Normal = Vector3.UnitZ,
        };
    }

    private static Toolpath BuildDualIslandToolpath(
        Vector3 wallACenter,
        Vector3 wallBCenter,
        float thicknessMm,
        float lengthMm,
        float bedZ,
        float layerH,
        int layerCount,
        float beadWidth)
    {
        float halfX = Math.Max(0.5f, thicknessMm * 0.5f - beadWidth * 0.25f);
        float halfY = Math.Max(2f, lengthMm * 0.5f - beadWidth * 0.25f);

        var toolpath = new Toolpath();
        Vector3? last = null;

        for (int li = 0; li < layerCount; li++)
        {
            float z = bedZ + layerH * (li + 1);
            var layer = new ToolpathLayer(li, z)
            {
                Height      = layerH,
                PlaneNormal = Vector3.UnitZ,
            };

            bool aFirst = li % 2 == 0;
            var firstCenter  = aFirst ? wallACenter : wallBCenter;
            var secondCenter = aFirst ? wallBCenter : wallACenter;

            var firstLoop  = RectangleLoop(firstCenter.X, firstCenter.Y, halfX, halfY, z);
            var secondLoop = RectangleLoop(secondCenter.X, secondCenter.Y, halfX, halfY, z);

            if (last is { } prev)
            {
                int travelIdx = layer.Moves.Count;
                layer.Moves.Add(new ToolpathMove(prev, firstLoop[0], MoveKind.Travel)
                {
                    Normal = Vector3.UnitZ,
                });
                AppendClosedLoop(layer, firstLoop, travelIdx);
            }
            else
            {
                AppendClosedLoop(layer, firstLoop, entryTravelIndex: -1);
            }

            {
                int travelIdx = layer.Moves.Count;
                layer.Moves.Add(new ToolpathMove(firstLoop[^1], secondLoop[0], MoveKind.Travel)
                {
                    Normal = Vector3.UnitZ,
                });
                AppendClosedLoop(layer, secondLoop, travelIdx);
            }

            last = secondLoop[^1];
            toolpath.Layers.Add(layer);
        }

        return toolpath;
    }

    private static Vector3[] RectangleLoop(float cx, float cy, float halfX, float halfY, float z) =>
    [
        new Vector3(cx - halfX, cy - halfY, z),
        new Vector3(cx + halfX, cy - halfY, z),
        new Vector3(cx + halfX, cy + halfY, z),
        new Vector3(cx - halfX, cy + halfY, z),
        new Vector3(cx - halfX, cy - halfY, z),
    ];

    private static void AppendClosedLoop(ToolpathLayer layer, Vector3[] loop, int entryTravelIndex)
    {
        int start = layer.Moves.Count;
        for (int i = 0; i < loop.Length - 1; i++)
        {
            layer.Moves.Add(new ToolpathMove(loop[i], loop[i + 1], MoveKind.Extrude)
            {
                Normal = Vector3.UnitZ,
            });
        }
        int count = layer.Moves.Count - start;
        layer.Contours.Add(new ContourSpan(start, count, Closed: true, EntryTravelIndex: entryTravelIndex));
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

    private static void WriteGridStl(
        string path,
        IReadOnlyList<(TestCell Test, Vector3 WallA, Vector3 WallB, float Cx, float Cy)> placements,
        float thicknessMm,
        float lengthMm,
        float heightMm)
    {
        float hx = thicknessMm * 0.5f;
        float hy = lengthMm * 0.5f;
        float hz = heightMm * 0.5f;
        int boxCount = placements.Count * 2;

        using var fs = File.Create(path);
        using var w  = new BinaryWriter(fs);
        w.Write(new byte[80]);
        w.Write((uint)(boxCount * 12));
        foreach (var (_, wallA, wallB, _, _) in placements)
        {
            WriteBox(w, wallA, hx, hy, hz);
            WriteBox(w, wallB, hx, hy, hz);
        }
    }

    /// <summary>
    /// Hollow thin-wall cylinders (annular prisms) for each circle cell — outer radius =
    /// <paramref name="outerRadiusMm"/>, wall thickness ≈ bead width.
    /// </summary>
    private static void WriteCircleGridStl(
        string path,
        IReadOnlyList<(TestCell Test, Vector3 WallA, Vector3 WallB, float Cx, float Cy)> placements,
        float outerRadiusMm,
        float beadWidthMm,
        float heightMm,
        int segments)
    {
        float wall = Math.Max(2f, beadWidthMm);
        float rOut = Math.Max(wall + 1f, outerRadiusMm);
        float rIn  = Math.Max(1f, rOut - wall);
        // Two rings per cell × (outer/inner/top/bottom) × 2 tris × segments.
        int trisPerRing = segments * 8;
        int triCount = placements.Count * 2 * trisPerRing;

        using var fs = File.Create(path);
        using var w  = new BinaryWriter(fs);
        w.Write(new byte[80]);
        w.Write((uint)triCount);

        float halfH = heightMm * 0.5f;
        foreach (var (_, wallA, wallB, _, _) in placements)
        {
            // Dual rings per cell (same as dual-wall A/B).
            WriteAnnulus(w, wallA.X, wallA.Y, rOut, rIn, wallA.Z - halfH, wallA.Z + halfH, segments);
            WriteAnnulus(w, wallB.X, wallB.Y, rOut, rIn, wallB.Z - halfH, wallB.Z + halfH, segments);
        }
    }

    private static void WriteAnnulus(
        BinaryWriter w, float cx, float cy, float rOut, float rIn, float za, float zb, int segments)
    {
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * (MathF.PI * 2f / segments);
            float a1 = (i + 1) * (MathF.PI * 2f / segments);
            float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0);
            float c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);

            var o0a = new Vector3(cx + rOut * c0, cy + rOut * s0, za);
            var o1a = new Vector3(cx + rOut * c1, cy + rOut * s1, za);
            var o0b = new Vector3(cx + rOut * c0, cy + rOut * s0, zb);
            var o1b = new Vector3(cx + rOut * c1, cy + rOut * s1, zb);
            var i0a = new Vector3(cx + rIn * c0, cy + rIn * s0, za);
            var i1a = new Vector3(cx + rIn * c1, cy + rIn * s1, za);
            var i0b = new Vector3(cx + rIn * c0, cy + rIn * s0, zb);
            var i1b = new Vector3(cx + rIn * c1, cy + rIn * s1, zb);

            // Outer wall
            WriteTri(w, o0a, o1a, o1b);
            WriteTri(w, o0a, o1b, o0b);
            // Inner wall (inward)
            WriteTri(w, i0a, i1b, i1a);
            WriteTri(w, i0a, i0b, i1b);
            // Top ring
            WriteTri(w, o0b, o1b, i1b);
            WriteTri(w, o0b, i1b, i0b);
            // Bottom ring
            WriteTri(w, o0a, i1a, o1a);
            WriteTri(w, o0a, i0a, i1a);
        }
    }

    private static void WriteBox(BinaryWriter w, Vector3 c, float hx, float hy, float hz)
    {
        var p = new Vector3[8];
        int n = 0;
        for (int dz = 0; dz < 2; dz++)
        for (int dy = 0; dy < 2; dy++)
        for (int dx = 0; dx < 2; dx++)
            p[n++] = c + new Vector3((dx * 2 - 1) * hx, (dy * 2 - 1) * hy, (dz * 2 - 1) * hz);

        int[][] faces =
        [
            [0, 2, 3, 0, 3, 1],
            [4, 5, 7, 4, 7, 6],
            [0, 1, 5, 0, 5, 4],
            [2, 6, 7, 2, 7, 3],
            [0, 4, 6, 0, 6, 2],
            [1, 3, 7, 1, 7, 5],
        ];

        foreach (var f in faces)
        {
            WriteTri(w, p[f[0]], p[f[1]], p[f[2]]);
            WriteTri(w, p[f[3]], p[f[4]], p[f[5]]);
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
