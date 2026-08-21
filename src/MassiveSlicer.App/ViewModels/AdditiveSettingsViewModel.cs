using System.Collections.ObjectModel;
using System.Globalization;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Parameters for additive (wire-arc / paste extrusion) slicing.
/// All dimensions are stored in millimetres; unit conversion for display
/// is handled at the binding layer.
/// </summary>
public sealed class AdditiveSettingsViewModel : ViewModelBase
{
    /// <summary>KRL SRC post-processing rules and header/footer templates.</summary>
    public KrlPostProcessSettingsViewModel KrlPostProcess { get; } = new();

    public AdditiveSettingsViewModel()
    {
        KrlPostProcess.Owner = this;
        SetDefaultHomePositionCommand = new RelayCommand(() => OnSetDefaultHomePositionRequested?.Invoke());
        ReverseTiltDirectionCommand      = new RelayCommand(ReverseTiltDirection);
        SetPatternCommand = new RelayCommand<string>(p => PatternType = p ?? "Smooth");
        AutoTiltCommand       = new RelayCommand(() => OnAutoTiltRequested?.Invoke(false), () => !IsAutoTiltRunning);
        AutoTiltRotateCommand = new RelayCommand(() => OnAutoTiltRequested?.Invoke(true),  () => !IsAutoTiltRunning);
        OpenSeamEditorCommand            = new RelayCommand(() => OnOpenSeamEditorRequested?.Invoke());
        SimulateThermalCommand           = new RelayCommand(() => OnSimulateThermalRequested?.Invoke());
        OptimizeToolpathCommand          = new RelayCommand(() => OnOptimizeToolpathRequested?.Invoke());
        foreach (var row in MultiPlanarPlanes) row.Owner = this;
        MultiPlanarPlanes.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (MultiPlanarPlaneRow row in e.NewItems) row.Owner = this;
        };
        OpenCurvedBoundaryEditorCommand  = new RelayCommand(() => OnOpenCurvedBoundaryEditorRequested?.Invoke());
        ImportCurvedBoundariesCommand    = new RelayCommand(() => OnImportCurvedBoundariesRequested?.Invoke());

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BeadWidth) or nameof(LayerHeight) or nameof(PrintSpeed)
                or nameof(SelectedPresetIndex) or nameof(ExtrusionSpeedOffset))
                OnPropertyChanged(nameof(ExtrusionSpeedPercent));

            // First-layer calculated speed/RPM depend on bead width, first-layer height,
            // print speed (= default first-layer speed) and material.
            if (e.PropertyName is nameof(BeadWidth) or nameof(FirstLayerHeight) or nameof(PrintSpeed)
                or nameof(SelectedPresetIndex))
            {
                OnPropertyChanged(nameof(FirstLayerSpeedCalculated));
                OnPropertyChanged(nameof(FirstLayerSpeedEffective));
                OnPropertyChanged(nameof(FirstLayerRpmCalculated));
                OnPropertyChanged(nameof(FirstLayerRpmEffective));
            }

            if (e.PropertyName is nameof(SelectedPresetIndex) or nameof(TemperatureOffset))
            {
                OnPropertyChanged(nameof(ExportTemperatureC));
                OnPropertyChanged(nameof(ExportTemperaturesLabel));
            }
        };
    }

    // -- Geometry -------------------------------------------------------------

    private double _layerHeight = 3.0;

    /// <summary>Height of each deposited layer in mm (0.5 - 100).</summary>
    public double LayerHeight
    {
        get => _layerHeight;
        set => SetField(ref _layerHeight, Math.Clamp(value, 0.5, 100.0));
    }

    private double _beadWidth = 6.0;

    /// <summary>Width of the deposited bead in mm (1 - 100).</summary>
    public double BeadWidth
    {
        get => _beadWidth;
        set => SetField(ref _beadWidth, Math.Clamp(value, 1.0, 100.0));
    }

    private bool   _useDisplacedStock;
    private double _stockAllowanceMm = 2.0;

    /// <summary>
    /// Print the displaced surface (low-poly mesh + PBR-map detail from the MILLING panel) instead of
    /// the raw mesh, so the blank carries the detail and the later mill has material to cut.
    /// </summary>
    public bool UseDisplacedStock
    {
        get => _useDisplacedStock;
        set => SetField(ref _useDisplacedStock, value);
    }

    /// <summary>Uniform extra skin added over the displaced surface for the mill to remove (mm).</summary>
    public double StockAllowanceMm
    {
        get => _stockAllowanceMm;
        set => SetField(ref _stockAllowanceMm, Math.Clamp(value, 0.0, 50.0));
    }

    private double _firstLayerHeight = 3.0;

    /// <summary>Override height for the first layer only, in mm.</summary>
    public double FirstLayerHeight
    {
        get => _firstLayerHeight;
        set => SetField(ref _firstLayerHeight, Math.Clamp(value, 0.5, 100.0));
    }

    /// <summary>Mirrors AdaptiveLayerHeight — layer-stripe preview is active whenever adaptive mode is on.</summary>
    /// <summary>
    /// Show the layer-thickness bands whenever SOME rule can vary thickness — not only when
    /// Adaptive layer height is on. Keyed to adaptive alone, the preview was hidden exactly when
    /// support-driven was doing all the thinning, which is the case a user most needs to see.
    /// </summary>
    public bool ShowLayerPreview
        => _adaptiveLayerHeight || _supportDrivenLayerHeight || _maxLayerHeightChangeMm > 1e-4;

    // -- Adaptive layer height ------------------------------------------------

    private bool _adaptiveLayerHeight;

    /// <summary>When true, the planar slicer adapts layer spacing to local surface slope.</summary>
    public bool AdaptiveLayerHeight
    {
        get => _adaptiveLayerHeight;
        set
        {
            if (SetField(ref _adaptiveLayerHeight, value))
            {
                OnPropertyChanged(nameof(ShowAdaptiveControls));
                OnPropertyChanged(nameof(ShowLayerPreview));
                OnPropertyChanged(nameof(ShowMinLayerHeight));
                OnPropertyChanged(nameof(ShowMaxLayerHeightChange));
                OnPropertyChanged(nameof(ShowLayerPreview));
            }
        }
    }

    /// <summary>Visible when adaptive is checked and method is Planar.</summary>
    public bool ShowAdaptiveControls => _adaptiveLayerHeight && _method == SliceMethod.Planar;

    /// <summary>
    /// Min layer height is the floor for EVERY rule that varies thickness, not just the adaptive
    /// one — support-driven thinning reads it as its floor too. It used to live inside the
    /// adaptive-only block, so turning adaptive off hid the one control deciding how thin
    /// support-driven could go while it silently kept governing the result.
    /// </summary>
    public bool ShowMinLayerHeight
        => (_adaptiveLayerHeight || _supportDrivenLayerHeight) && _method == SliceMethod.Planar;

    /// <summary>Visible when method is Planar (for the checkbox itself).</summary>
    public bool ShowAdaptiveLayerHeight => _method == SliceMethod.Planar;

    // -- Slicing mode (Normal vs Surface) -------------------------------------

    public string[] SlicingModeOptions { get; } = ["Normal", "Surface"];

    private string _slicingMode = "Normal";

    /// <summary>
    /// Normal = volumetric shells + infill. Surface = boundary/cladding paths; tool stays vertical unless Overhang orientation is on.
    /// </summary>
    public string SlicingMode
    {
        get => _slicingMode;
        set
        {
            if (SetField(ref _slicingMode, value))
            {
                OnPropertyChanged(nameof(ShowInfillControls));
                OnPropertyChanged(nameof(SurfaceModeActive));
            }
        }
    }

    public bool SurfaceModeActive => _slicingMode == "Surface";

    /// <summary>Surface mode is for planar/angled strategies (not geodesic/curved).</summary>
    public bool ShowSlicingMode => _method is not SliceMethod.Geodesic and not SliceMethod.Curved;

    private double _adaptiveQuality = 0.5;

    /// <summary>0 = finest detail (thin layers on slopes), 1 = fastest (thick layers where possible).</summary>
    public double AdaptiveQuality
    {
        get => _adaptiveQuality;
        set => SetField(ref _adaptiveQuality, Math.Clamp(value, 0.0, 1.0));
    }

    private double _minLayerHeight = 1.0;

    /// <summary>Minimum layer height used by adaptive slicing (mm).</summary>
    public double MinLayerHeight
    {
        get => _minLayerHeight;
        set => SetField(ref _minLayerHeight, Math.Clamp(value, 0.1, 100.0));
    }

    private bool _proximityCorrectionEnabled;

    /// <summary>
    /// Reduce flow where two beads on the same layer run alongside each other closer than a bead
    /// width. Intended to become automatic — the toggle exists so it can be A/B'd on a print first.
    /// </summary>
    public bool ProximityCorrectionEnabled
    {
        get => _proximityCorrectionEnabled;
        set => SetField(ref _proximityCorrectionEnabled, value);
    }

    private double _proximityMinRunLengthMm = 100.0;

    /// <summary>
    /// Shortest continuous crowded stretch worth correcting (mm). Deliberately NOT in the panel —
    /// the measured populations separate with an empty band from 60 to 250 mm, so any value in
    /// there behaves identically and there is nothing for a user to tune. Reachable via
    /// <c>addset</c> for testing.
    /// </summary>
    public double ProximityMinRunLengthMm
    {
        get => _proximityMinRunLengthMm;
        set => SetField(ref _proximityMinRunLengthMm, Math.Clamp(value, 0.0, 10000.0));
    }

    private double _maxFlowChangePercentPerSecond = 2.0;

    /// <summary>
    /// Fastest the commanded flow may change WITHIN a layer, as a percent of the current value per
    /// second. 0 = no limit.
    ///
    /// Deliberately defaults ON, unlike the proximity toggle it protects: stamping a correction on
    /// in one step is what saturated the extruder drive, so shipping this off would ship the
    /// failure. 2 %/s is what a known-good export from another cell actually does — 5 % per 2.5 s.
    /// Not in the panel while the real limit is unknown; reachable via <c>addset</c> and reported by
    /// the <c>flow-slew</c> console command.
    /// </summary>
    public double MaxFlowChangePercentPerSecond
    {
        get => _maxFlowChangePercentPerSecond;
        set => SetField(ref _maxFlowChangePercentPerSecond, Math.Clamp(value, 0.0, 100.0));
    }

    private bool _proximityHoldThroughStructure = true;

    /// <summary>
    /// Hold the reduced flow across a whole structure instead of ramping back up between its crowded
    /// features. On by default; not in the panel, reachable via <c>addset</c> for A/B against a print.
    /// </summary>
    public bool ProximityHoldThroughStructure
    {
        get => _proximityHoldThroughStructure;
        set => SetField(ref _proximityHoldThroughStructure, value);
    }

    private double _maxLayerHeightChangeMm;

    /// <summary>
    /// Largest thickness change allowed between adjacent layers (mm). 0 = off.
    ///
    /// Both height rules pick each layer independently, so nothing otherwise stops 4.00 -> 2.61 ->
    /// 4.00 on consecutive layers. RPM follows real thickness while speed usually does not, so a
    /// thickness cliff reaches the machine as an RPM cliff. Only ever THINS, so the stairstep
    /// tolerance and the overlap target both still hold.
    /// </summary>
    public double MaxLayerHeightChangeMm
    {
        get => _maxLayerHeightChangeMm;
        set => SetField(ref _maxLayerHeightChangeMm, Math.Clamp(value, 0.0, 100.0));
    }

    /// <summary>
    /// Visible whenever a rule can vary thickness — the cap only has something to smooth then.
    /// Mirrors <see cref="ShowMinLayerHeight"/> deliberately: a control that silently governs a
    /// result while hidden is exactly how Min layer height went missing before.
    /// </summary>
    public bool ShowMaxLayerHeightChange
        => (_adaptiveLayerHeight || _supportDrivenLayerHeight) && _method == SliceMethod.Planar;

    private double _adaptiveMinFaceAreaMm2;

    /// <summary>
    /// Smallest triangle (mm²) allowed to dictate a layer's thickness.
    /// 0 = derive it from the bead footprint; negative = off, every triangle votes.
    ///
    /// Triangle SIZE is not physically meaningful to stairstepping — a shallow surface steps the
    /// same whether it is one big triangle or a thousand small ones. Size is a proxy for whether
    /// the triangle's NORMAL can be trusted: a real shallow feature occupies real area, whereas a
    /// sliver's orientation is an artefact of how the file was tessellated, and a triangle smaller
    /// than the bead describes a feature the machine cannot print anyway.
    /// </summary>
    public double AdaptiveMinFaceAreaMm2
    {
        get => _adaptiveMinFaceAreaMm2;
        set => SetField(ref _adaptiveMinFaceAreaMm2, Math.Clamp(value, -1.0, 100000.0));
    }

    private bool   _supportDrivenLayerHeight;
    private double _supportOverlapTargetPercent = 60.0;
    private double _supportBridgeToleranceMm;

    /// <summary>
    /// Thin a layer when the boundary steps sideways far enough that the bead would not sit on the
    /// one below. Off by default — it changes slice output. Composes with adaptive layer height by
    /// taking the thinner, so it can only ever make a layer thinner, never thicker.
    /// </summary>
    public bool SupportDrivenLayerHeight
    {
        get => _supportDrivenLayerHeight;
        set
        {
            if (SetField(ref _supportDrivenLayerHeight, value))
                OnPropertyChanged(nameof(ShowMinLayerHeight));
                OnPropertyChanged(nameof(ShowMaxLayerHeightChange));
                OnPropertyChanged(nameof(ShowLayerPreview));
        }
    }

    /// <summary>
    /// How much of each bead must sit on the one below (%). 60 means it may hang off by 40 % of its
    /// width. 50 is the stated minimum; 60 is the overcorrection so an under-extruding bead still
    /// lands safe.
    /// </summary>
    public double SupportOverlapTargetPercent
    {
        get => _supportOverlapTargetPercent;
        set => SetField(ref _supportOverlapTargetPercent, Math.Clamp(value, 0.0, 100.0));
    }

    /// <summary>
    /// How long a continuous under-target stretch may be before the layer is thinned (mm).
    /// 0 = derive as 2 x bead width. An absolute length, not a share of the layer — bridging is a
    /// local property, and 1 % of a long layer is not the same defect as 1 % of a short one.
    /// </summary>
    public double SupportBridgeToleranceMm
    {
        get => _supportBridgeToleranceMm;
        set => SetField(ref _supportBridgeToleranceMm, Math.Clamp(value, 0.0, 1000.0));
    }

    // -- Slicing method -------------------------------------------------------

    private SliceMethod _method = SliceMethod.Planar;

    /// <summary>Which slicing algorithm to use.</summary>
    public SliceMethod Method
    {
        get => _method;
        set
        {
            if (SetField(ref _method, value))
            {
                OnPropertyChanged(nameof(MethodDisplayName));
                OnPropertyChanged(nameof(ShowTiltAngle));
                OnPropertyChanged(nameof(ShowMultiPlanarControls));
                OnPropertyChanged(nameof(ShowContourOffsetOption));
                OnPropertyChanged(nameof(ShowPlanarSeamExtras));
                OnPropertyChanged(nameof(ShowAdaptiveLayerHeight));
                OnPropertyChanged(nameof(ShowAdaptiveControls));
                OnPropertyChanged(nameof(ShowMinLayerHeight));
                OnPropertyChanged(nameof(ShowMaxLayerHeightChange));
                OnPropertyChanged(nameof(ShowLayerPreview));
                OnPropertyChanged(nameof(ShowSlicingMode));
                OnPropertyChanged(nameof(ShowCurvedControls));
                OnPropertyChanged(nameof(IsCurvedMethod));
                OnPropertyChanged(nameof(ShowOrientationFollow));
                OnPropertyChanged(nameof(ShowLayerLean));
            }
        }
    }

    public string[] AvailableMethodNames { get; } =
        ["Planar", "Angled", "Multi-Planar", "Geodesic (Experimental)", "Curved (Sweep)"];

    public string MethodDisplayName
    {
        get => Method switch
        {
            SliceMethod.Angled      => "Angled",
            SliceMethod.MultiPlanar => "Multi-Planar",
            SliceMethod.Geodesic    => "Geodesic (Experimental)",
            SliceMethod.Curved      => "Curved (Sweep)",
            _                       => "Planar",
        };
        set => Method = value switch
        {
            "Angled"                  => SliceMethod.Angled,
            "Multi-Planar"            => SliceMethod.MultiPlanar,
            "MultiPlanar"             => SliceMethod.MultiPlanar,
            "Geodesic (Experimental)" => SliceMethod.Geodesic,
            "Geodesic"                => SliceMethod.Geodesic,
            "Curved (Sweep)"          => SliceMethod.Curved,
            "Curved"                  => SliceMethod.Curved,
            _                         => SliceMethod.Planar,
        };
    }

    /// <summary>Multi-Planar guide plane stack (height % + tilt °), min two rows.</summary>
    public System.Collections.ObjectModel.ObservableCollection<MultiPlanarPlaneRow> MultiPlanarPlanes { get; } =
    [
        new(0, 0), new(50, 15), new(100, 30),
    ];

    /// <summary>Bumped whenever any plane row (or the stack shape) changes —
    /// registered as a realtime re-slice trigger.</summary>
    public int MultiPlanarStamp
    {
        get => _multiPlanarStamp;
        private set => SetField(ref _multiPlanarStamp, value);
    }
    private int _multiPlanarStamp;

    internal void BumpMultiPlanarStamp() => MultiPlanarStamp++;

    private bool _multiPlanarAxisX;
    /// <summary>False = tilt about Y (lean along X); true = tilt about X (lean along Y).</summary>
    public bool MultiPlanarAxisX
    {
        get => _multiPlanarAxisX;
        set { if (SetField(ref _multiPlanarAxisX, value)) BumpMultiPlanarStamp(); }
    }

    public RelayCommand AddMultiPlanarPlaneCommand => _addMultiPlanarPlane ??= new RelayCommand(() =>
    {
        // Insert into the biggest height gap so new planes land somewhere useful.
        var sorted = MultiPlanarPlanes.OrderBy(r => r.HeightPct).ToList();
        double bestGap = -1, at = 50, angle = 15;
        for (int i = 1; i < sorted.Count; i++)
        {
            double gap = sorted[i].HeightPct - sorted[i - 1].HeightPct;
            if (gap > bestGap)
            {
                bestGap = gap;
                at = (sorted[i].HeightPct + sorted[i - 1].HeightPct) * 0.5;
                angle = (sorted[i].AngleDeg + sorted[i - 1].AngleDeg) * 0.5;
            }
        }
        MultiPlanarPlanes.Add(new MultiPlanarPlaneRow(Math.Round(at), Math.Round(angle, 1)));
        BumpMultiPlanarStamp();
    });
    private RelayCommand? _addMultiPlanarPlane;

    public void RemoveMultiPlanarPlane(MultiPlanarPlaneRow row)
    {
        if (MultiPlanarPlanes.Count <= 2) return;   // interpolation needs two ends
        MultiPlanarPlanes.Remove(row);
        BumpMultiPlanarStamp();
    }

    public bool IsCurvedMethod          => Method == SliceMethod.Curved;
    public bool ShowCurvedControls      => Method == SliceMethod.Curved;
    /// <summary>Surface-follow (vertical ↔ stacking-normal tween) applies to methods that
    /// emit per-move surface normals: Geodesic and Curved (Sweep).</summary>
    public bool ShowOrientationFollow   => Method is SliceMethod.Geodesic or SliceMethod.Curved;
    /// <summary>Layer-lean (previous-layer tilt) applies to plane-stacked methods.</summary>
    public bool ShowLayerLean           => Method is SliceMethod.Planar or SliceMethod.Angled;
    public bool ShowTiltAngle           => Method == SliceMethod.Angled;
    public bool ShowMultiPlanarControls => Method == SliceMethod.MultiPlanar;
    /// <summary>Bead-width contour inset — planar / angled / multi-planar only.</summary>
    public bool ShowContourOffsetOption => Method is not SliceMethod.Geodesic and not SliceMethod.Curved;
    /// <summary>
    /// Seam guides + spiral extras that only apply to planar-style slicing.
    /// Geodesic / Curved still show SEAM for Zig-zag mode.
    /// </summary>
    public bool ShowPlanarSeamExtras => Method is not SliceMethod.Geodesic and not SliceMethod.Curved;

    private bool _disableContourOffset;

    /// <summary>When true, skips the bead-width/2 inset so the raw contour is the centerline.</summary>
    public bool DisableContourOffset
    {
        get => _disableContourOffset;
        set => SetField(ref _disableContourOffset, value);
    }

    public string[] SeamModeOptions { get; } = ["Normal", "Zig-zag", "Spiral (vase)"];

    private string _seamMode = "Normal";

    /// <summary>
    /// Normal = closed loops / standard seams.
    /// Zig-zag = single-skin open wall: longest face only (no back panel), reverse
    /// direction every layer (end of line → Z up → reverse).
    /// Spiral = vase mode continuous Z.
    /// </summary>
    public string SeamMode
    {
        get => _seamMode;
        set
        {
            if (SetField(ref _seamMode, value))
                OnPropertyChanged(nameof(ShowZigZagTravelOption));
        }
    }

    private bool _zigZagAllowSameLayerTravel = true;

    /// <summary>
    /// Zig-zag only: keep multiple open faces on one layer and Travel (start/stop)
    /// between them. Off = print only the longest open face per layer.
    /// </summary>
    public bool ZigZagAllowSameLayerTravel
    {
        get => _zigZagAllowSameLayerTravel;
        set => SetField(ref _zigZagAllowSameLayerTravel, value);
    }

    public bool ShowZigZagTravelOption =>
        string.Equals(_seamMode, "Zig-zag", StringComparison.OrdinalIgnoreCase);

    /// <summary>World-space seam position guides for planar slicing.</summary>
    public ObservableCollection<SeamGuidePoint> SeamGuides { get; } = [];

    public string SeamGuideSummary =>
        SeamGuides.Count == 0 ? "No guides" : $"{SeamGuides.Count} guide point(s)";

    public void SetSeamGuides(IEnumerable<SeamGuidePoint> guides)
    {
        SeamGuides.Clear();
        foreach (var g in guides)
            SeamGuides.Add(g);
        OnPropertyChanged(nameof(SeamGuideSummary));
    }

    public IReadOnlyList<SeamGuidePoint> BuildSeamGuideList() => [.. SeamGuides];

    // ── Toolpath paint marks (brush tool) ──────────────────────────────────────

    /// <summary>World-space brush dabs painted on the toolpath (Bridge = grow
    /// fingers under the beads; Remove = delete the beads). Persist with the
    /// workspace; survive re-slices because they are world-space spheres.</summary>
    public List<Core.Models.PaintMark> PaintMarks { get; } = [];

    /// <summary>Bumped when a paint stroke commits — registered as a realtime
    /// re-slice trigger.</summary>
    public int PaintStamp
    {
        get => _paintStamp;
        private set => SetField(ref _paintStamp, value);
    }
    private int _paintStamp;

    internal void BumpPaintStamp() => PaintStamp++;

    public void SetPaintMarks(IEnumerable<Core.Models.PaintMark> marks)
    {
        PaintMarks.Clear();
        PaintMarks.AddRange(marks);
        BumpPaintStamp();
    }

    public IReadOnlyList<Core.Models.PaintMark> BuildPaintMarkList() => [.. PaintMarks];

    // ── Structural Supports (2×4 pockets / cylinder wraps in the wall path) ──────

    public List<Core.Models.StructuralSupportSpec> StructuralSupports { get; } = [];

    public IReadOnlyList<Core.Models.StructuralSupportSpec> BuildStructuralSupportList()
        => [.. StructuralSupports];

    private int _selectedSupportIndex = -1;
    public int SelectedSupportIndex
    {
        get => _selectedSupportIndex;
        set
        {
            if (!SetField(ref _selectedSupportIndex, value)) return;
            NotifySelectedSupportChanged();
        }
    }

    public bool HasStructuralSupports => StructuralSupports.Count > 0;
    public string StructuralSupportsLabel =>
        StructuralSupports.Count == 1 ? "1 support" : $"{StructuralSupports.Count} supports";

    public string[] SupportShapeOptions { get; } = ["Rectangle", "Circle"];

    Core.Models.StructuralSupportSpec? SelectedSupport =>
        _selectedSupportIndex >= 0 && _selectedSupportIndex < StructuralSupports.Count
            ? StructuralSupports[_selectedSupportIndex] : null;

    void ReplaceSelected(Core.Models.StructuralSupportSpec spec)
    {
        if (_selectedSupportIndex < 0 || _selectedSupportIndex >= StructuralSupports.Count) return;
        StructuralSupports[_selectedSupportIndex] = spec;
        NotifySelectedSupportChanged();
    }

    void NotifySelectedSupportChanged()
    {
        OnPropertyChanged(nameof(SupportShape));
        OnPropertyChanged(nameof(SupportCenterX));
        OnPropertyChanged(nameof(SupportCenterY));
        OnPropertyChanged(nameof(SupportWidthMm));
        OnPropertyChanged(nameof(SupportDepthMm));
        OnPropertyChanged(nameof(SupportRotationDeg));
        OnPropertyChanged(nameof(SupportLayersUp));
        OnPropertyChanged(nameof(SupportLayersDown));
        OnPropertyChanged(nameof(SupportEnabled));
        OnPropertyChanged(nameof(HasStructuralSupports));
        OnPropertyChanged(nameof(StructuralSupportsLabel));
        OnStructuralSupportsChanged?.Invoke();
    }

    /// <summary>Fired on any support add/edit/remove — viewport redraws the helpers.</summary>
    internal Action? OnStructuralSupportsChanged { get; set; }

    public string SupportShape
    {
        get => SelectedSupport?.Shape == Core.Models.SupportShapeKind.Circle ? "Circle" : "Rectangle";
        set
        {
            if (SelectedSupport is { } s)
                ReplaceSelected(s with
                {
                    Shape = value == "Circle"
                        ? Core.Models.SupportShapeKind.Circle
                        : Core.Models.SupportShapeKind.Rectangle,
                });
        }
    }

    public double SupportCenterX
    {
        get => SelectedSupport?.CenterX ?? 0;
        set { if (SelectedSupport is { } s) ReplaceSelected(s with { CenterX = (float)value }); }
    }

    public double SupportCenterY
    {
        get => SelectedSupport?.CenterY ?? 0;
        set { if (SelectedSupport is { } s) ReplaceSelected(s with { CenterY = (float)value }); }
    }

    public double SupportWidthMm
    {
        get => SelectedSupport?.WidthMm ?? 92;
        set { if (SelectedSupport is { } s) ReplaceSelected(s with { WidthMm = (float)Math.Clamp(value, 5, 2000) }); }
    }

    public double SupportDepthMm
    {
        get => SelectedSupport?.DepthMm ?? 42;
        set { if (SelectedSupport is { } s) ReplaceSelected(s with { DepthMm = (float)Math.Clamp(value, 5, 2000) }); }
    }

    public double SupportRotationDeg
    {
        get => SelectedSupport?.RotationDeg ?? 0;
        set { if (SelectedSupport is { } s) ReplaceSelected(s with { RotationDeg = (float)value }); }
    }

    public int SupportLayersUp
    {
        get => SelectedSupport?.LayersUp ?? 9999;
        set { if (SelectedSupport is { } s) ReplaceSelected(s with { LayersUp = Math.Max(0, value) }); }
    }

    public int SupportLayersDown
    {
        get => SelectedSupport?.LayersDown ?? 0;
        set { if (SelectedSupport is { } s) ReplaceSelected(s with { LayersDown = Math.Max(0, value) }); }
    }

    public bool SupportEnabled
    {
        get => SelectedSupport?.Enabled ?? true;
        set { if (SelectedSupport is { } s) ReplaceSelected(s with { Enabled = value }); }
    }

    internal void AddStructuralSupport(Core.Models.StructuralSupportSpec spec)
    {
        StructuralSupports.Add(spec);
        SelectedSupportIndex = StructuralSupports.Count - 1;
        NotifySelectedSupportChanged();
    }

    private RelayCommand? _removeSupportCmd;
    public RelayCommand RemoveSelectedSupportCommand => _removeSupportCmd ??= new RelayCommand(() =>
        RemoveStructuralSupportAt(_selectedSupportIndex));

    internal void RemoveStructuralSupportAt(int index)
    {
        if (index < 0 || index >= StructuralSupports.Count) return;
        StructuralSupports.RemoveAt(index);
        if (_selectedSupportIndex >= index)
            _selectedSupportIndex = Math.Min(_selectedSupportIndex, StructuralSupports.Count - 1);
        OnPropertyChanged(nameof(SelectedSupportIndex));
        NotifySelectedSupportChanged();
    }

    private RelayCommand? _prevSupportCmd;
    public RelayCommand PrevSupportCommand => _prevSupportCmd ??= new RelayCommand(() =>
    {
        if (StructuralSupports.Count == 0) return;
        SelectedSupportIndex = (_selectedSupportIndex - 1 + StructuralSupports.Count) % StructuralSupports.Count;
    });

    private RelayCommand? _nextSupportCmd;
    public RelayCommand NextSupportCommand => _nextSupportCmd ??= new RelayCommand(() =>
    {
        if (StructuralSupports.Count == 0) return;
        SelectedSupportIndex = (_selectedSupportIndex + 1) % StructuralSupports.Count;
    });

    /// <summary>Clears every painted mark (both kinds) and re-slices.</summary>
    public RelayCommand ClearPaintMarksCommand => _clearPaintMarks ??= new RelayCommand(() =>
    {
        if (PaintMarks.Count == 0) return;
        PaintMarks.Clear();
        BumpPaintStamp();
    });
    private RelayCommand? _clearPaintMarks;

    /// <summary>Opens the viewport seam guide editor.</summary>
    public RelayCommand OpenSeamEditorCommand { get; }

    internal Action? OnOpenSeamEditorRequested { get; set; }

    /// <summary>Raised to open the KRL Post-Processing dialog (the KRL EXPORT gear, or "krlpost open").</summary>
    internal Action? OnOpenKrlPostProcessRequested { get; set; }

    /// <summary>Opens the KRL Post-Processing dialog. Returns false when no view is attached.</summary>
    internal bool RequestOpenKrlPostProcess()
    {
        if (OnOpenKrlPostProcessRequested is null) return false;
        OnOpenKrlPostProcessRequested();
        return true;
    }

    /// <summary>Runs the analytical thermomechanical screen and fills Adaptive Speed low/high.</summary>
    public RelayCommand SimulateThermalCommand { get; }

    internal Action? OnSimulateThermalRequested { get; set; }

    /// <summary>Re-orders the sliced toolpath to minimize travel and bridges nearby paths into one extrusion.</summary>
    public RelayCommand OptimizeToolpathCommand { get; }

    internal Action? OnOptimizeToolpathRequested { get; set; }

    private string _optimizeToolpathSummary = "";
    /// <summary>Human-readable result of the last toolpath optimization.</summary>
    public string OptimizeToolpathSummary
    {
        get => _optimizeToolpathSummary;
        set => SetField(ref _optimizeToolpathSummary, value);
    }

    private string _thermalSummary = "";
    /// <summary>Human-readable result of the last thermomechanical simulation.</summary>
    public string ThermalSummary
    {
        get => _thermalSummary;
        set { if (SetField(ref _thermalSummary, value)) OnPropertyChanged(nameof(HasThermalSummary)); }
    }

    public bool HasThermalSummary => !string.IsNullOrEmpty(_thermalSummary);

    // -- Curved slicing boundaries --------------------------------------------

    public string[] CurvedBoundarySourceOptions { get; } = ["Auto", "Viewport Pick", "JSON Import"];

    private string _curvedBoundarySource = "Auto";

    public string CurvedBoundarySourceDisplay
    {
        get => _curvedBoundarySource;
        set
        {
            if (SetField(ref _curvedBoundarySource, value))
                OnPropertyChanged(nameof(CurvedBoundarySummary));
        }
    }

    public CurvedBoundarySource CurvedBoundarySource => _curvedBoundarySource switch
    {
        "Viewport Pick" => CurvedBoundarySource.ViewportPick,
        "JSON Import"   => CurvedBoundarySource.JsonImport,
        _               => CurvedBoundarySource.AutoDetect,
    };

    private double _curvedAutoDetectBandMm = 2.0;

    public double CurvedAutoDetectBandMm
    {
        get => _curvedAutoDetectBandMm;
        set => SetField(ref _curvedAutoDetectBandMm, Math.Clamp(value, 0.1, 50.0));
    }

    private bool _curvedEnableRegionSplit = true;

    public bool CurvedEnableRegionSplit
    {
        get => _curvedEnableRegionSplit;
        set => SetField(ref _curvedEnableRegionSplit, value);
    }

    public ObservableCollection<int> CurvedBoundaryLowVertices  { get; } = [];
    public ObservableCollection<int> CurvedBoundaryHighVertices { get; } = [];

    public string CurvedBoundarySummary =>
        $"LOW: {CurvedBoundaryLowVertices.Count} verts, HIGH: {CurvedBoundaryHighVertices.Count} verts";

    public void SetCurvedBoundaries(IEnumerable<int> low, IEnumerable<int> high)
    {
        CurvedBoundaryLowVertices.Clear();
        CurvedBoundaryHighVertices.Clear();
        foreach (var v in low)  CurvedBoundaryLowVertices.Add(v);
        foreach (var v in high) CurvedBoundaryHighVertices.Add(v);
        OnPropertyChanged(nameof(CurvedBoundarySummary));
    }

    public IReadOnlyList<int> BuildCurvedLowBoundaryList()  => [.. CurvedBoundaryLowVertices];
    public IReadOnlyList<int> BuildCurvedHighBoundaryList() => [.. CurvedBoundaryHighVertices];

    public RelayCommand OpenCurvedBoundaryEditorCommand { get; }
    public RelayCommand ImportCurvedBoundariesCommand { get; }

    internal Action? OnOpenCurvedBoundaryEditorRequested { get; set; }
    internal Func<Task>? OnImportCurvedBoundariesRequested { get; set; }

    private double _passAngle;

    /// <summary>Rotation of each pass relative to the previous, in degrees (Planar/Angled).</summary>
    public double PassAngle
    {
        get => _passAngle;
        set => SetField(ref _passAngle, value);
    }

    // ── Live effector ──────────────────────────────────────────────────────
    private bool _effectorEnabled;
    /// <summary>Master toggle for the live effector points.</summary>
    public bool EffectorEnabled
    {
        get => _effectorEnabled;
        set => SetField(ref _effectorEnabled, value);
    }

    public string[] EffectorModeOptions { get; } = ["Amplify", "Erase (smooth)"];

    private string _effectorMode = "Amplify";
    /// <summary>What the effector does in its influence area: boost the pattern
    /// amplitude, or erase it back to a plain wall.</summary>
    public string EffectorMode
    {
        get => _effectorMode;
        set
        {
            if (SetField(ref _effectorMode, value))
                OnPropertyChanged(nameof(IsEffectorAmplify));
        }
    }

    public bool IsEffectorAmplify => !_effectorMode.StartsWith("Erase", StringComparison.OrdinalIgnoreCase);

    private double _effectorRange = 400.0;
    /// <summary>Effector influence radius (mm).</summary>
    public double EffectorRange
    {
        get => _effectorRange;
        set => SetField(ref _effectorRange, Math.Clamp(value, 10.0, 3000.0));
    }

    private double _effectorStrength = 30.0;
    /// <summary>Amplitude boost at the effector centre (mm).</summary>
    public double EffectorStrength
    {
        get => _effectorStrength;
        set => SetField(ref _effectorStrength, Math.Clamp(value, 0.0, 200.0));
    }

    // ── Pattern & texture (effector port) ─────────────────────────────────
    private string _patternType = "Smooth";
    /// <summary>Selected decorative pattern name (matches Core PatternType).</summary>
    public string PatternType
    {
        get => _patternType;
        set => SetField(ref _patternType, value);
    }

    public string[] PatternScopeOptions { get; } =
        ["Everything", "Walls only (no infill/bracing)", "Visible skin (raycast)"];

    private string _patternScope = "Everything";
    /// <summary>
    /// How far Wave/Pattern reach into the part. Anything left out stays straight, but its ends
    /// still ride the wall so it stays bonded.
    /// </summary>
    public string PatternScope
    {
        get => _patternScope;
        set => SetField(ref _patternScope, value);
    }

    public string[] PatternMappingOptions { get; } =
        ["Wavelength (mm)", "Even (path length)", "Radial (angle)"];

    private string _patternMapping = "Wavelength (mm)";
    /// <summary>How the pattern wraps the part: fixed mm wavelength, even per-loop, or polar angle.</summary>
    public string PatternMapping
    {
        get => _patternMapping;
        set
        {
            if (SetField(ref _patternMapping, value))
                OnPropertyChanged(nameof(ShowPatternWavelength));
        }
    }

    public bool ShowPatternWavelength => _patternMapping.StartsWith("Wavelength", StringComparison.OrdinalIgnoreCase);

    private double _patternWavelengthMm = 60.0;
    /// <summary>Cycle size in mm for wavelength mapping.</summary>
    public double PatternWavelengthMm
    {
        get => _patternWavelengthMm;
        set => SetField(ref _patternWavelengthMm, Math.Clamp(value, 2.0, 2000.0));
    }

    private double _patternAmplitude;
    /// <summary>Pattern relief depth in mm (0 = off).</summary>
    public double PatternAmplitude
    {
        get => _patternAmplitude;
        set => SetField(ref _patternAmplitude, Math.Clamp(value, 0.0, 100.0));
    }

    private double _patternFrequency = 15.0;
    /// <summary>Pattern repetitions around the part.</summary>
    public double PatternFrequency
    {
        get => _patternFrequency;
        set => SetField(ref _patternFrequency, Math.Clamp(value, 1.0, 120.0));
    }

    private double _patternTwist;
    /// <summary>Pattern twist in degrees per mm of height.</summary>
    public double PatternTwist
    {
        get => _patternTwist;
        set => SetField(ref _patternTwist, Math.Clamp(value, -5.0, 5.0));
    }

    private double _patternOffset;
    /// <summary>Pattern phase offset in degrees.</summary>
    public double PatternOffset
    {
        get => _patternOffset;
        set => SetField(ref _patternOffset, Math.Clamp(value, 0.0, 360.0));
    }

    private double _patternFadeIn;
    /// <summary>Pattern ease-in from the bottom (mm).</summary>
    public double PatternFadeIn
    {
        get => _patternFadeIn;
        set => SetField(ref _patternFadeIn, Math.Clamp(value, 0.0, 2000.0));
    }

    private double _patternFadeOut;
    /// <summary>Pattern ease-out to the top (mm).</summary>
    public double PatternFadeOut
    {
        get => _patternFadeOut;
        set => SetField(ref _patternFadeOut, Math.Clamp(value, 0.0, 2000.0));
    }

    /// <summary>Selects a pattern tile.</summary>
    public RelayCommand<string> SetPatternCommand { get; private set; } = null!;

    private double _tiltAngle;

    /// <summary>Tilt around Y-axis in degrees (leans the cutting plane toward ±X).</summary>
    public double TiltAngle
    {
        get => _tiltAngle;
        set => SetField(ref _tiltAngle, Math.Clamp(value, -89.0, 89.0));
    }

    private double _tiltAngleX;

    /// <summary>Tilt around X-axis in degrees (leans the cutting plane toward ±Y).</summary>
    public double TiltAngleX
    {
        get => _tiltAngleX;
        set => SetField(ref _tiltAngleX, Math.Clamp(value, -89.0, 89.0));
    }

    /// <summary>Reverses the angled-slice direction by flipping both tilt angles.</summary>
    public RelayCommand ReverseTiltDirectionCommand { get; }

    private void ReverseTiltDirection()
    {
        TiltAngle  = -TiltAngle;
        TiltAngleX = -TiltAngleX;
    }

    /// <summary>Auto-calculates the X/Y tilt with the least overhang risk (mesh stays put).</summary>
    public RelayCommand AutoTiltCommand { get; }

    /// <summary>Auto-calculates the optimal slice direction, yaw-rotating the mesh so it becomes a pure Y tilt.</summary>
    public RelayCommand AutoTiltRotateCommand { get; }

    /// <summary>Raised by the auto-tilt commands; the viewport handles the mesh analysis.
    /// Argument: true = also rotate the mesh (dropdown option), false = tilt only.</summary>
    internal Action<bool>? OnAutoTiltRequested { get; set; }

    private bool _isAutoTiltRunning;

    /// <summary>True while the auto-tilt analysis runs — disables both commands.</summary>
    public bool IsAutoTiltRunning
    {
        get => _isAutoTiltRunning;
        set
        {
            if (!SetField(ref _isAutoTiltRunning, value)) return;
            AutoTiltCommand.RaiseCanExecuteChanged();
            AutoTiltRotateCommand.RaiseCanExecuteChanged();
        }
    }

    // -- Motion ---------------------------------------------------------------

    private double _printSpeed = 100.0;

    /// <summary>Deposition print speed in mm/s.</summary>
    public double PrintSpeed
    {
        get => _printSpeed;
        set => SetField(ref _printSpeed, Math.Clamp(value, 1.0, 2000.0));
    }

    private double _travelSpeed = 600.0;

    /// <summary>Travel (non-extrusion) move speed in mm/s.</summary>
    public double TravelSpeed
    {
        get => _travelSpeed;
        set => SetField(ref _travelSpeed, Math.Clamp(value, 1.0, 2000.0));
    }

    private double _apoCvel;

    /// <summary>
    /// KUKA $APO.CVEL value (0–100). Controls the minimum speed fraction the robot
    /// must maintain through corners. 50 = slow to at most 50% of programmed speed
    /// at a sharp turn; 100 = maintain full speed (no blending slowdown).
    /// Written to the SRC header via the <c>{{APO_CVEL}}</c> placeholder, and also
    /// used by the simulation velocity profile behind the print-time estimate.
    /// Edited on the KRL Post-Processing dialog's Rules tab.
    /// </summary>
    public double ApoCvel
    {
        get => _apoCvel;
        set
        {
            if (!SetField(ref _apoCvel, Math.Clamp(value, 0.0, 100.0))) return;
            // The field lives on the KRL dialog's Rules tab and proxies back to here.
            KrlPostProcess.NotifyApoCvelChanged();
        }
    }

    private int _acceleration = 100;

    /// <summary>Acceleration as a percentage of robot-rated maximum (1 - 100).</summary>
    public int Acceleration
    {
        get => _acceleration;
        set => SetField(ref _acceleration, Math.Clamp(value, 1, 100));
    }

    private double _approachZ = 50.0;

    /// <summary>Z height above the part to approach before each pass, in mm.</summary>
    public double ApproachZ
    {
        get => _approachZ;
        set => SetField(ref _approachZ, value);
    }

    // -- KUKA frame indices ----------------------------------------------------

    private int _toolDataIndex = 1;

    /// <summary>KUKA TOOL_DATA index (1 - 16) used in the generated KRL program.</summary>
    public int ToolDataIndex
    {
        get => _toolDataIndex;
        set => SetField(ref _toolDataIndex, Math.Clamp(value, 1, 16));
    }

    private int _baseDataIndex = 1;

    /// <summary>KUKA BASE_DATA index (1 - 32) used in the generated KRL program.</summary>
    public int BaseDataIndex
    {
        get => _baseDataIndex;
        set => SetField(ref _baseDataIndex, Math.Clamp(value, 1, 32));
    }

    // -- Brim (bed adhesion) -------------------------------------------------------

    private bool _brimEnabled;
    /// <summary>Outward offset loops around the first layer for bed adhesion (applied last, encloses X-bracing).</summary>
    public bool BrimEnabled
    {
        get => _brimEnabled;
        set
        {
            if (SetField(ref _brimEnabled, value))
                OnPropertyChanged(nameof(ShowBrimControls));
        }
    }

    public bool ShowBrimControls => BrimEnabled;

    private int _brimLoops = 3;
    /// <summary>Number of brim offset loops (one bead width apart).</summary>
    public int BrimLoops
    {
        get => _brimLoops;
        set => SetField(ref _brimLoops, Math.Clamp(value, 1, 50));
    }

    private double _brimSpeed = SliceSettings.MaxBrimSpeedMmS;
    /// <summary>
    /// Fixed brim speed (mm/s). Deliberately ignores print speed and the Adaptive Speed
    /// window — the brim is bed adhesion, not part shape. Capped at
    /// <see cref="SliceSettings.MaxBrimSpeedMmS"/>.
    /// </summary>
    public double BrimSpeed
    {
        get => _brimSpeed;
        set => SetField(ref _brimSpeed, Math.Clamp(value, 1.0, SliceSettings.MaxBrimSpeedMmS));
    }

    private double _brimRpmPercent;
    /// <summary>
    /// Absolute brim extrusion RPM (%). 0 = off, i.e. RPM follows brim speed as usual.
    /// Raise it to lay a deliberately fat brim for adhesion despite the slow brim speed —
    /// this value bypasses every per-move flow scale. Capped at
    /// <see cref="SliceSettings.MaxBrimRpmPercent"/> so it cannot trip the export gate.
    /// </summary>
    public double BrimRpmPercent
    {
        get => _brimRpmPercent;
        set => SetField(ref _brimRpmPercent,
                        value <= 0.0 ? 0.0 : Math.Clamp(value, 1.0, SliceSettings.MaxBrimRpmPercent));
    }

    // -- X-Bracing Wall ----------------------------------------------------------

    private bool _xBracingEnabled;
    /// <summary>Cut dual-wall X braces into the perimeter for structural back-support.</summary>
    public bool XBracingEnabled
    {
        get => _xBracingEnabled;
        set
        {
            if (SetField(ref _xBracingEnabled, value))
            {
                OnPropertyChanged(nameof(ShowXBracingControls));
                OnPropertyChanged(nameof(ShowXBracingPlanarControls));
                OnPropertyChanged(nameof(ShowXBracingCylinderControls));
                OnPropertyChanged(nameof(ShowXBracingDepthEase));
            }
        }
    }

    public bool ShowXBracingControls => XBracingEnabled;

    public string[] XBracingProjectionTypeOptions { get; } = ["Planar", "Cylinder"];

    private string _xBracingProjectionType = "Planar";
    /// <summary>Planar = oriented plane; Cylinder = radial from a vertical cylinder on the bed.</summary>
    public string XBracingProjectionType
    {
        get => _xBracingProjectionType;
        set
        {
            if (SetField(ref _xBracingProjectionType, value is "Cylinder" ? "Cylinder" : "Planar"))
            {
                OnPropertyChanged(nameof(ShowXBracingPlanarControls));
                OnPropertyChanged(nameof(ShowXBracingCylinderControls));
                // When the cylinder is put into the scene, centre it on the print bed
                // (not the robot / world origin).
                if (XBracingProjectionType == "Cylinder")
                    PlaceCylinderAtPrintBedCenter(keepDiameter: true);
            }
        }
    }

    public bool ShowXBracingPlanarControls => XBracingEnabled && XBracingProjectionType != "Cylinder";
    public bool ShowXBracingCylinderControls => XBracingEnabled && XBracingProjectionType == "Cylinder";

    private bool _xBracingShowHelper = true;
    /// <summary>Show the brace plane / cylinder helper in the viewport (visual only).
    /// Persisted with app prefs and the .mass workspace (Settings + UiSession).</summary>
    public bool XBracingShowHelper
    {
        get => _xBracingShowHelper;
        set => SetField(ref _xBracingShowHelper, value);
    }

    private double _xBracingDepthMm = 50.0;
    /// <summary>Brace depth at the TOP of the part (mm).</summary>
    public double XBracingDepthMm
    {
        get => _xBracingDepthMm;
        set => SetField(ref _xBracingDepthMm, Math.Clamp(value, 5.0, 500.0));
    }

    private double _xBracingDepthBottomMm;
    /// <summary>Brace depth at the BOTTOM of the part (mm). 0 = constant depth
    /// (same as <see cref="XBracingDepthMm"/>); &gt; 0 tapers over height with ease modes.</summary>
    public double XBracingDepthBottomMm
    {
        get => _xBracingDepthBottomMm;
        set
        {
            if (SetField(ref _xBracingDepthBottomMm, value <= 0.0 ? 0.0 : Math.Clamp(value, 5.0, 500.0)))
                OnPropertyChanged(nameof(ShowXBracingDepthEase));
        }
    }

    /// <summary>True when bottom depth is set so the height taper (and ease) is active.</summary>
    public bool ShowXBracingDepthEase => XBracingEnabled && _xBracingDepthBottomMm > 0.01;

    public string[] XBracingDepthEaseOptions { get; } =
        ["Linear", "Ease-In", "Ease-Out", "Smooth"];

    private string _xBracingDepthEaseBottom = "Linear";
    /// <summary>Depth-taper ease at the bottom (start of the height curve).</summary>
    public string XBracingDepthEaseBottom
    {
        get => _xBracingDepthEaseBottom;
        set => SetField(ref _xBracingDepthEaseBottom,
            XBracingDepthEaseOptions.Contains(value) ? value : "Linear");
    }

    private string _xBracingDepthEaseTop = "Linear";
    /// <summary>Depth-taper ease at the top (end of the height curve).</summary>
    public string XBracingDepthEaseTop
    {
        get => _xBracingDepthEaseTop;
        set => SetField(ref _xBracingDepthEaseTop,
            XBracingDepthEaseOptions.Contains(value) ? value : "Linear");
    }

    private double _xBracingSpanMm = 120.0;
    /// <summary>Horizontal span of one full X cell along the wall (mm).</summary>
    public double XBracingSpanMm
    {
        get => _xBracingSpanMm;
        set => SetField(ref _xBracingSpanMm, Math.Clamp(value, 20.0, 2000.0));
    }

    private double _xBracingAngleDeg = 30.0;
    /// <summary>Brace angle from vertical (deg). Lower = more printable.</summary>
    public double XBracingAngleDeg
    {
        get => _xBracingAngleDeg;
        set => SetField(ref _xBracingAngleDeg, Math.Clamp(value, 10.0, 60.0));
    }

    private bool _xBracingExtendEdges = true;
    /// <summary>Partial X cells on left/right ends (never top/bottom of the part).</summary>
    public bool XBracingExtendEdges
    {
        get => _xBracingExtendEdges;
        set => SetField(ref _xBracingExtendEdges, value);
    }

    private double _xBracingPlaneTiltY;
    /// <summary>
    /// Orientable brace plane: tilt about Y (°). Hairpins grow perpendicular to this
    /// plane (along its normal projected into the layer). 0/0 = horizontal — falls
    /// back to path left-normal.
    /// </summary>
    public double XBracingPlaneTiltY
    {
        get => _xBracingPlaneTiltY;
        set => SetField(ref _xBracingPlaneTiltY, Math.Clamp(value, -90.0, 90.0));
    }

    private double _xBracingPlaneTiltX;
    /// <summary>Brace plane tilt about X (°). See <see cref="XBracingPlaneTiltY"/>.</summary>
    public double XBracingPlaneTiltX
    {
        get => _xBracingPlaneTiltX;
        set => SetField(ref _xBracingPlaneTiltX, Math.Clamp(value, -90.0, 90.0));
    }

    public RelayCommand FlipXBracingPlaneCommand => _flipXBracingPlane ??= new RelayCommand(() =>
    {
        // Negating both tilts flips the plane normal (brace direction).
        XBracingPlaneTiltY = -XBracingPlaneTiltY;
        XBracingPlaneTiltX = -XBracingPlaneTiltX;
    });
    private RelayCommand? _flipXBracingPlane;

    public RelayCommand ResetXBracingPlaneCommand => _resetXBracingPlane ??= new RelayCommand(() =>
    {
        XBracingPlaneTiltY = 0;
        XBracingPlaneTiltX = 0;
    });
    private RelayCommand? _resetXBracingPlane;

    private double _xBracingCylinderDiameterMm = 200.0;
    /// <summary>Cylinder projection diameter (mm). Height follows the model AABB.</summary>
    public double XBracingCylinderDiameterMm
    {
        get => _xBracingCylinderDiameterMm;
        set => SetField(ref _xBracingCylinderDiameterMm, Math.Clamp(value, 10.0, 20000.0));
    }

    private double _xBracingCylinderX;
    /// <summary>Cylinder axis X on the bed (mm, world).</summary>
    public double XBracingCylinderX
    {
        get => _xBracingCylinderX;
        set => SetField(ref _xBracingCylinderX, value);
    }

    private double _xBracingCylinderY;
    /// <summary>Cylinder axis Y on the bed (mm, world).</summary>
    public double XBracingCylinderY
    {
        get => _xBracingCylinderY;
        set => SetField(ref _xBracingCylinderY, value);
    }

    private bool _xBracingCylinderFlipDirection;
    /// <summary>
    /// Default off: braces pull toward the cylinder axis.
    /// On: braces radiate outward from the axis.
    /// </summary>
    public bool XBracingCylinderFlipDirection
    {
        get => _xBracingCylinderFlipDirection;
        set => SetField(ref _xBracingCylinderFlipDirection, value);
    }

    /// <summary>
    /// Resolves print-bed surface centre XY in world mm (from the active cell).
    /// Wired by MainWindowViewModel; null when no cell is loaded.
    /// </summary>
    public Func<(double X, double Y)?>? ResolvePrintBedCenterXY { get; set; }

    /// <summary>Places the brace cylinder on the print-bed centre (not robot origin).</summary>
    public void PlaceCylinderAtPrintBedCenter(bool keepDiameter = false)
    {
        if (ResolvePrintBedCenterXY?.Invoke() is { } c)
        {
            XBracingCylinderX = c.X;
            XBracingCylinderY = c.Y;
        }
        else
        {
            // No cell yet — leave XY as-is rather than forcing robot origin.
        }
        if (!keepDiameter)
            XBracingCylinderDiameterMm = 200;
        XBracingCylinderFlipDirection = false;
    }

    public RelayCommand ResetXBracingCylinderCommand => _resetXBracingCylinder ??= new RelayCommand(() =>
        PlaceCylinderAtPrintBedCenter(keepDiameter: false));
    private RelayCommand? _resetXBracingCylinder;

    // -- Wave effect -------------------------------------------------------------

    public string[] WaveEffectOptions { get; } = ["None", "Sine", "Sawtooth", "Triangle"];

    private string _waveEffect = "None";

    /// <summary>Selected wave effect type. "None" disables the effect.</summary>
    public string WaveEffect
    {
        get => _waveEffect;
        set
        {
            if (SetField(ref _waveEffect, value))
                OnPropertyChanged(nameof(ShowWaveControls));
        }
    }

    public bool ShowWaveControls => WaveEffect != "None";

    private double _waveAmplitude = 3.0;

    /// <summary>Peak displacement in mm (0.5 – 100).</summary>
    public double WaveAmplitude
    {
        get => _waveAmplitude;
        set => SetField(ref _waveAmplitude, Math.Clamp(value, 0.5, 100.0));
    }

    public string[] WaveFrequencyModeOptions { get; } = ["Wavelength", "Cycles"];

    private string _waveFrequencyMode = "Wavelength";

    public string WaveFrequencyMode
    {
        get => _waveFrequencyMode;
        set
        {
            if (SetField(ref _waveFrequencyMode, value))
            {
                OnPropertyChanged(nameof(ShowWavelengthInput));
                OnPropertyChanged(nameof(ShowCyclesInput));
                OnPropertyChanged(nameof(ShowSingleWavelength));
                OnPropertyChanged(nameof(ShowGradientWavelength));
            }
        }
    }

    public bool ShowWavelengthInput => WaveFrequencyMode == "Wavelength";
    public bool ShowCyclesInput     => WaveFrequencyMode == "Cycles";

    private double _waveWavelength = 20.0;

    /// <summary>Length of one complete wave cycle in mm (1 – 1000).</summary>
    public double WaveWavelength
    {
        get => _waveWavelength;
        set => SetField(ref _waveWavelength, Math.Clamp(value, 1.0, 1000.0));
    }

    private int _waveCycles = 8;

    /// <summary>Fixed number of complete wave cycles per layer (1 – 500). Used when WaveFrequencyMode == Cycles.</summary>
    public int WaveCycles
    {
        get => _waveCycles;
        set => SetField(ref _waveCycles, Math.Clamp(value, 1, 500));
    }

    private double _waveShape = 1.0;

    /// <summary>Wave shape: 1.0 = full waveform, lower clips peaks toward a square wave (0.01 – 1.0).</summary>
    public double WaveShape
    {
        get => _waveShape;
        set => SetField(ref _waveShape, Math.Clamp(value, 0.01, 1.0));
    }

    private double _waveStagger = 0.0;

    /// <summary>
    /// Phase offset per layer as a fraction of one wavelength (0 – 1).
    /// 0 = all layers identical. 0.5 = consecutive layers alternate peak/valley.
    /// </summary>
    public double WaveStagger
    {
        get => _waveStagger;
        set => SetField(ref _waveStagger, Math.Clamp(value, 0.0, 1.0));
    }

    private int _wavePhaseMethodIndex;   // 0 = Method A, 1 = Method B

    /// <summary>
    /// Wave phase method (dropdown index). 0 = Method A (seam anchored, original),
    /// 1 = Method B (phase inheritance).
    /// </summary>
    public int WavePhaseMethodIndex
    {
        get => _wavePhaseMethodIndex;
        set => SetField(ref _wavePhaseMethodIndex, Math.Clamp(value, 0, 1));
    }

    /// <summary>"A" or "B" — the value passed to SliceSettings.</summary>
    public string WavePhaseMethod => _wavePhaseMethodIndex == 1 ? "B" : "A";

    // -- Wave gradient ----------------------------------------------------------

    private bool _waveGradient;

    public bool WaveGradient
    {
        get => _waveGradient;
        set
        {
            if (SetField(ref _waveGradient, value))
            {
                OnPropertyChanged(nameof(ShowWaveGradientControls));
                OnPropertyChanged(nameof(ShowSingleAmplitude));
                OnPropertyChanged(nameof(ShowSingleWavelength));
                OnPropertyChanged(nameof(ShowGradientWavelength));
            }
        }
    }

    public bool ShowWaveGradientControls => WaveGradient;
    public bool ShowSingleAmplitude      => !WaveGradient;
    public bool ShowSingleWavelength     => ShowWavelengthInput && !WaveGradient;
    public bool ShowGradientWavelength   => ShowWavelengthInput && WaveGradient;

    private double _waveAmplitudeBottom = 0.0;
    public double WaveAmplitudeBottom
    {
        get => _waveAmplitudeBottom;
        set => SetField(ref _waveAmplitudeBottom, Math.Clamp(value, 0.0, 100.0));
    }

    private double _waveAmplitudeTop = 3.0;
    public double WaveAmplitudeTop
    {
        get => _waveAmplitudeTop;
        set => SetField(ref _waveAmplitudeTop, Math.Clamp(value, 0.0, 100.0));
    }

    private double _waveWavelengthBottom = 20.0;
    public double WaveWavelengthBottom
    {
        get => _waveWavelengthBottom;
        set => SetField(ref _waveWavelengthBottom, Math.Clamp(value, 1.0, 1000.0));
    }

    private double _waveWavelengthTop = 20.0;
    public double WaveWavelengthTop
    {
        get => _waveWavelengthTop;
        set => SetField(ref _waveWavelengthTop, Math.Clamp(value, 1.0, 1000.0));
    }

    private double _waveGradientCenter = 0.5;
    public double WaveGradientCenter
    {
        get => _waveGradientCenter;
        set => SetField(ref _waveGradientCenter, Math.Clamp(value, 0.001, 0.999));
    }

    public string[] WaveGradientCurveOptions { get; } = ["Linear", "Smooth", "Ease In", "Ease Out"];

    private string _waveGradientCurve = "Linear";
    public string WaveGradientCurve
    {
        get => _waveGradientCurve;
        set => SetField(ref _waveGradientCurve, value);
    }

    // -- Infill pattern -------------------------------------------------------

    public string[] InfillPatternOptions { get; } =
        ["None", "Rectilinear", "Grid", "Triangle", "Ghost Mesh Grid", "Formbound Bridge", "Formbound Buttress"];

    private string _infillPattern = "None";

    /// <summary>Selected infill pattern. "None" = emit shells as normal.</summary>
    public string InfillPattern
    {
        get => _infillPattern;
        set
        {
            if (SetField(ref _infillPattern, value))
            {
                OnPropertyChanged(nameof(ShowInfillControls));
                OnPropertyChanged(nameof(ShowGridControls));
                OnPropertyChanged(nameof(ShowLightningControls));
                OnPropertyChanged(nameof(ShowButtressControls));
            }
        }
    }

    public bool ShowInfillControls => InfillPattern != "None";

    /// <summary>Grid width / angle only apply to the line-based fills — Formbound
    /// patterns are demand-driven and ignore them.</summary>
    public bool ShowGridControls =>
        InfillPattern is "Rectilinear" or "Grid" or "Triangle" or "Ghost Mesh Grid";

    public bool ShowLightningControls => InfillPattern is "Formbound Bridge" or "Lightning Bridge" or "Formbound Buttress";

    public bool ShowButtressControls => InfillPattern is "Formbound Buttress";

    private double _lightningOverhangDeg = 30.0;
    /// <summary>Max unsupported overhang angle for lightning finger growth (deg).</summary>
    public double LightningOverhangDeg
    {
        get => _lightningOverhangDeg;
        set => SetField(ref _lightningOverhangDeg, Math.Clamp(value, 5.0, 80.0));
    }

    private double _lightningBranchSpacingMm;
    /// <summary>Spacing between finger roots along unsupported arcs (mm). 0 = auto.</summary>
    public double LightningBranchSpacingMm
    {
        get => _lightningBranchSpacingMm;
        set => SetField(ref _lightningBranchSpacingMm, Math.Clamp(value, 0.0, 500.0));
    }

    private double _lightningTipLoopRadiusMm;
    /// <summary>Support-pad loop radius at finger tips (mm). 0 = plain tip.</summary>
    public double LightningTipLoopRadiusMm
    {
        get => _lightningTipLoopRadiusMm;
        set => SetField(ref _lightningTipLoopRadiusMm, Math.Clamp(value, 0.0, 200.0));
    }

    private bool _lightningAnchorInterior = true;
    private bool _lightningAnchorExterior;
    private bool _lightningExteriorOverhangs;
    private bool _lightningPreferInteriorMouths = true;

    /// <summary>
    /// Support inward overhangs / cavities / inner walls. Own tip budget.
    /// Also enables interior mouth anchoring (notches hidden inside the part).
    /// </summary>
    public bool LightningAffectInterior
    {
        get => _lightningAnchorInterior;
        set
        {
            if (!SetField(ref _lightningAnchorInterior, value)) return;
            OnPropertyChanged(nameof(LightningAnchorInterior));
        }
    }

    /// <summary>
    /// Support outward flares / free edges (sacrificial exterior fins). Own tip budget,
    /// independent of Interior — both may be on at once.
    /// </summary>
    public bool LightningAffectExterior
    {
        get => _lightningExteriorOverhangs;
        set
        {
            if (!SetField(ref _lightningExteriorOverhangs, value)) return;
            // Exterior demand uses exterior perimeter mouths.
            if (SetField(ref _lightningAnchorExterior, value))
                OnPropertyChanged(nameof(LightningAnchorExterior));
            OnPropertyChanged(nameof(LightningExteriorOverhangs));
        }
    }

    /// <summary>Persistence / planner: root fingers on interior boundaries.</summary>
    public bool LightningAnchorInterior
    {
        get => _lightningAnchorInterior;
        set
        {
            if (!SetField(ref _lightningAnchorInterior, value)) return;
            OnPropertyChanged(nameof(LightningAffectInterior));
        }
    }

    /// <summary>Persistence / planner: root fingers on the outer perimeter.</summary>
    public bool LightningAnchorExterior
    {
        get => _lightningAnchorExterior;
        set
        {
            if (!SetField(ref _lightningAnchorExterior, value)) return;
            // Keep exterior-affect demand in sync when loaded from prefs.
            if (value && !_lightningExteriorOverhangs)
            {
                _lightningExteriorOverhangs = true;
                OnPropertyChanged(nameof(LightningExteriorOverhangs));
                OnPropertyChanged(nameof(LightningAffectExterior));
            }
            else if (!value && _lightningExteriorOverhangs && !_lightningAnchorInterior)
            {
                // exterior-only mode can leave AnchorExterior true; no force-off of demand
            }
            OnPropertyChanged(nameof(LightningAffectExterior));
        }
    }

    /// <summary>Planner: grow sacrificial fins under outward overhangs.</summary>
    public bool LightningExteriorOverhangs
    {
        get => _lightningExteriorOverhangs;
        set
        {
            if (!SetField(ref _lightningExteriorOverhangs, value)) return;
            if (value && !_lightningAnchorExterior)
            {
                _lightningAnchorExterior = true;
                OnPropertyChanged(nameof(LightningAnchorExterior));
            }
            OnPropertyChanged(nameof(LightningAffectExterior));
        }
    }

    private double _lightningButtressBarMm = 40.0;
    /// <summary>Formbound Buttress: single-bead horizontal support bar length (mm).</summary>
    public double LightningButtressBarMm
    {
        get => _lightningButtressBarMm;
        set => SetField(ref _lightningButtressBarMm, Math.Clamp(value, 5.0, 500.0));
    }

    /// <summary>Formbound Buttress: prefer interior mouths when Interior domain is enabled.</summary>
    public bool LightningPreferInteriorMouths
    {
        get => _lightningPreferInteriorMouths;
        set => SetField(ref _lightningPreferInteriorMouths, value);
    }

    private bool _lightningTargetSupportSelections;
    /// <summary>
    /// Formbound Bridge/Buttress: only place support under edit-mode Support
    /// selections (painted Bridge marks). Disables automatic overhang detection.
    /// </summary>
    public bool LightningTargetSupportSelections
    {
        get => _lightningTargetSupportSelections;
        set => SetField(ref _lightningTargetSupportSelections, value);
    }

    private double _infillSpacingMm = 0.0;

    /// <summary>Centre-to-centre infill line spacing in mm. 0 = use bead width.</summary>
    public double InfillSpacingMm
    {
        get => _infillSpacingMm;
        set => SetField(ref _infillSpacingMm, Math.Clamp(value, 0.0, 500.0));
    }

    private double _infillAngleDeg = 0.0;

    /// <summary>Base angle of infill lines in degrees (0 = parallel to X axis).</summary>
    public double InfillAngleDeg
    {
        get => _infillAngleDeg;
        set => SetField(ref _infillAngleDeg, value % 360.0);
    }

    // -- Overhang orientation -------------------------------------------------

    private bool _overhangOrientation;

    /// <summary>When true, the planar slicer tilts the toolhead to follow mesh surface normals.</summary>
    public bool OverhangOrientation
    {
        get => _overhangOrientation;
        set
        {
            if (SetField(ref _overhangOrientation, value))
                OnPropertyChanged(nameof(ShowOverhangTilt));
        }
    }

    public bool ShowOverhangTilt => _overhangOrientation;

    private double _maxOverhangTiltDeg = 45.0;

    /// <summary>Maximum tool tilt from vertical in degrees (0 – 89).</summary>
    public double MaxOverhangTiltDeg
    {
        get => _maxOverhangTiltDeg;
        set => SetField(ref _maxOverhangTiltDeg, Math.Clamp(value, 0.0, 89.0));
    }

    // -- Surface follow (vertical ↔ path-normal blend) --------------------------

    private double _orientationFollowPercent = 100.0;

    /// <summary>
    /// How strongly the tool follows surface/stacking normals (0–100%).
    /// 0 = vertical (world +Z), 100 = full path/surface follow.
    /// </summary>
    public double OrientationFollowPercent
    {
        get => _orientationFollowPercent;
        set => SetField(ref _orientationFollowPercent, Math.Clamp(value, 0.0, 100.0));
    }

    public float OrientationFollowStrength => (float)(OrientationFollowPercent / 100.0);

    private double _orientationMaxTiltDeg = 90.0;

    /// <summary>Hard cap on TCP tilt from vertical in degrees, applied after the
    /// surface-follow blend (90 = uncapped).</summary>
    public double OrientationMaxTiltDeg
    {
        get => _orientationMaxTiltDeg;
        set => SetField(ref _orientationMaxTiltDeg, Math.Clamp(value, 0.0, 90.0));
    }

    private bool _firstLayerZeroTilt;

    /// <summary>Force the first layer's tool orientation to vertical (flat-bed adhesion).</summary>
    public bool FirstLayerZeroTilt
    {
        get => _firstLayerZeroTilt;
        set => SetField(ref _firstLayerZeroTilt, value);
    }

    // -- Layer lean ("poor man's non-planar" for planar slicing) ----------------

    private double _layerLeanPercent;

    /// <summary>0–100: how strongly planar moves lean toward the previous layer. 0 = off.</summary>
    public double LayerLeanPercent
    {
        get => _layerLeanPercent;
        set => SetField(ref _layerLeanPercent, Math.Clamp(value, 0.0, 100.0));
    }

    private double _layerLeanMaxTiltDeg = 0.0;

    /// <summary>Hard cap on layer-lean tilt from vertical (degrees).</summary>
    public double LayerLeanMaxTiltDeg
    {
        get => _layerLeanMaxTiltDeg;
        set => SetField(ref _layerLeanMaxTiltDeg, Math.Clamp(value, 0.0, 90.0));
    }

    // -- Orientation smoothing ------------------------------------------------

    private bool _smoothRotation;

    public bool SmoothRotation
    {
        get => _smoothRotation;
        set
        {
            if (SetField(ref _smoothRotation, value))
                OnPropertyChanged(nameof(ShowSmoothRotationRadius));
        }
    }

    public bool ShowSmoothRotationRadius => _smoothRotation;

    private int _smoothRotationRadius = 5;

    /// <summary>Half-width of the smoothing window in moves (1 – 50).</summary>
    public int SmoothRotationRadius
    {
        get => _smoothRotationRadius;
        set => SetField(ref _smoothRotationRadius, Math.Clamp(value, 1, 50));
    }

    private double _smoothRotationMaxRateDegPerMm = 0.0;

    /// <summary>
    /// Maximum orientation change in degrees per mm of travel.
    /// Clamps the rate of toolhead rotation to prevent KUKA axis overspeed at sharp turns.
    /// 0 = disabled.
    /// </summary>
    public double SmoothRotationMaxRateDegPerMm
    {
        get => _smoothRotationMaxRateDegPerMm;
        set => SetField(ref _smoothRotationMaxRateDegPerMm, Math.Clamp(value, 0.0, 90.0));
    }

    private double _orientationLookAheadMm = 0.0;

    /// <summary>
    /// Forward look-ahead distance (mm) for the KRL exporter's Gaussian normal-smoothing kernel.
    /// At 60 mm/s print speed, 60 mm = 1 second of pre-rotation. 0 = disabled.
    /// </summary>
    public double OrientationLookAheadMm
    {
        get => _orientationLookAheadMm;
        set => SetField(ref _orientationLookAheadMm, Math.Clamp(value, 0.0, 500.0));
    }

    private double _orientationSigmaMm = 30.0;

    /// <summary>
    /// Gaussian sigma (mm) for the KRL exporter's normal-smoothing kernel.
    /// Controls the width of the orientation transition ramp. Typically half of OrientationLookAheadMm.
    /// </summary>
    public double OrientationSigmaMm
    {
        get => _orientationSigmaMm;
        set => SetField(ref _orientationSigmaMm, Math.Clamp(value, 1.0, 200.0));
    }

    // -- Toolhead approach orientation -----------------------------------------
    // These ABC angles (KUKA ZYX Euler, degrees) define the target tool orientation
    // used by the IK solver when scrubbing through a toolpath.  They are analogous
    // to the "toolhead ABC" setting in Eidos CAM: a fixed approach orientation applied
    // uniformly to every toolpath point.
    //
    // Defaults: A=0, B=0, C=0 -- identity (no additional rotation).
    // With these defaults the IK behaviour is identical to before this setting was added.
    // Increasing A rotates the tool around its own approach axis (e.g. spin the nozzle);
    // B/C tilt the tool relative to the plane-normal-derived approach direction.

    private double _toolheadA = 0.0;

    /// <summary>KUKA A angle (deg, rotation about Z) applied locally after the
    /// plane-normal-derived orientation. 0deg = no additional rotation.</summary>
    public double ToolheadA
    {
        get => _toolheadA;
        set => SetField(ref _toolheadA, Math.Clamp(value, -180.0, 180.0));
    }

    private double _toolheadB = 0.0;

    /// <summary>KUKA B angle (deg, rotation about Y') applied locally after the
    /// plane-normal-derived orientation. 0deg = no additional rotation.</summary>
    public double ToolheadB
    {
        get => _toolheadB;
        set => SetField(ref _toolheadB, Math.Clamp(value, -180.0, 180.0));
    }

    private double _toolheadC = 0.0;

    /// <summary>KUKA C angle (deg, rotation about X'') applied locally after the
    /// plane-normal-derived orientation. 0deg = no additional rotation.</summary>
    public double ToolheadC
    {
        get => _toolheadC;
        set => SetField(ref _toolheadC, Math.Clamp(value, -180.0, 180.0));
    }

    private bool _e1MotionEnabled;

    /// <summary>
    /// LFAM linear rail (E1): when true, export/validation let the carriage track the
    /// path within <see cref="E1YPlusMm"/> / <see cref="E1YMinusMm"/> of the home E1
    /// to reduce arm kinematic strain (especially with tilted toolhead).
    /// </summary>
    public bool E1MotionEnabled
    {
        get => _e1MotionEnabled;
        set
        {
            if (SetField(ref _e1MotionEnabled, value))
                OnPropertyChanged(nameof(ShowE1AllowanceControls));
        }
    }

    private double _e1YPlusMm = 500.0;

    /// <summary>Max E1 travel (mm) in the positive direction from home.</summary>
    public double E1YPlusMm
    {
        get => _e1YPlusMm;
        set => SetField(ref _e1YPlusMm, Math.Clamp(value, 0.0, 10000.0));
    }

    private double _e1YMinusMm = 500.0;

    /// <summary>Max E1 travel (mm) in the negative direction from home.</summary>
    public double E1YMinusMm
    {
        get => _e1YMinusMm;
        set => SetField(ref _e1YMinusMm, Math.Clamp(value, 0.0, 10000.0));
    }

    public bool ShowE1AllowanceControls => _e1MotionEnabled;

    // -- Material temperatures -------------------------------------------------

    // T1/T2/T3 are set by the selected material preset (see ApplyPreset).
    // They are not shown in the ADDITIVE tab; the TOOLPATH tab's material dropdown drives them.
    // Defaults to 230deg C when no material is selected.

    private double _temperature1 = 230.0;
    public double Temperature1
    {
        get => _temperature1;
        set => SetField(ref _temperature1, Math.Clamp(value, 0.0, 450.0));
    }

    private double _temperature2 = 230.0;
    public double Temperature2
    {
        get => _temperature2;
        set => SetField(ref _temperature2, Math.Clamp(value, 0.0, 450.0));
    }

    private double _temperature3 = 230.0;
    public double Temperature3
    {
        get => _temperature3;
        set => SetField(ref _temperature3, Math.Clamp(value, 0.0, 450.0));
    }

    // -- Material presets ------------------------------------------------------

    /// <summary>User's saved material preset library. Loaded at startup, persisted on each add.</summary>
    public ObservableCollection<MaterialPreset> MaterialPresets { get; } = [];

    private int _selectedPresetIndex = -1;

    /// <summary>
    /// Index of the selected material preset, or -1 for none.
    /// Setting this applies the preset's temperatures to T1/T2/T3.
    /// </summary>
    public int SelectedPresetIndex
    {
        get => _selectedPresetIndex;
        set
        {
            if (!SetField(ref _selectedPresetIndex, value)) return;
            OnPropertyChanged(nameof(HasSelectedPreset));
            if (value >= 0 && value < MaterialPresets.Count)
                ApplyPreset(MaterialPresets[value]);
        }
    }

    public bool HasSelectedPreset => _selectedPresetIndex >= 0 && _selectedPresetIndex < MaterialPresets.Count;

    /// <summary>Active material preset, or <c>null</c> when none is selected.</summary>
    public MaterialPreset? SelectedPreset =>
        HasSelectedPreset ? MaterialPresets[_selectedPresetIndex] : null;

    private void ApplyPreset(MaterialPreset p)
    {
        Temperature1 = p.Temperature1;
        Temperature2 = p.Temperature2;
        Temperature3 = p.Temperature3;
        OnPropertyChanged(nameof(ExtrusionSpeedPercent));
        OnPropertyChanged(nameof(ExportTemperatureC));
        OnPropertyChanged(nameof(ExportTemperaturesLabel));
    }

    // -- KRL export (Toolpath tab) ---------------------------------------------

    private string _temperatureOffset = "";

    /// <summary>±°C adjustment applied to all extruder zones at export. Empty = no change.</summary>
    public string TemperatureOffset
    {
        get => _temperatureOffset;
        set => SetField(ref _temperatureOffset, value);
    }

    /// <summary>Material preset temperature (°C) shown for all zones before offset.</summary>
    public double ExportTemperatureC => Temperature1;

    /// <summary>Material setpoints per zone, e.g. "290 / 290 / 300" — export base before offset.</summary>
    public string ExportTemperaturesLabel =>
        $"{Temperature1:F0}/{Temperature2:F0}/{Temperature3:F0}";

    /// <summary>Zone temperature (°C) written to KRL: the MATERIAL SETPOINT for that zone
    /// plus the all-zones <see cref="TemperatureOffset"/> (e.g. +10 raises every zone 10°).</summary>
    public float GetEffectiveExportTemperature(int zone = 1)
    {
        double baseT = zone switch { 2 => Temperature2, 3 => Temperature3, _ => Temperature1 };
        float temp = (float)baseT + (float)ParseSignedOffset(_temperatureOffset);
        return Math.Clamp(temp, 0f, 450f);
    }

    private string _extrusionSpeedOffset = "";

    /// <summary>±% adjustment applied to computed extrusion speed at export. Empty = no change.</summary>
    public string ExtrusionSpeedOffset
    {
        get => _extrusionSpeedOffset;
        set => SetField(ref _extrusionSpeedOffset, value);
    }

    private bool _activeExtruderIsHf;

    /// <summary>True when the active cell's extruder is the HF head, so the preset's HF flow rate
    /// is used instead of the HV one. Set from the active cell/tool in MainWindowViewModel.</summary>
    public bool ActiveExtruderIsHf
    {
        get => _activeExtruderIsHf;
        set
        {
            if (SetField(ref _activeExtruderIsHf, value))
                OnPropertyChanged(nameof(ExtrusionSpeedPercent));
        }
    }

    /// <summary>Computed extrusion motor speed (%) from bead geometry and material flow.</summary>
    public double ExtrusionSpeedPercent => ComputeExtrusionSpeedPercent();

    /// <summary>
    /// Flow rate (rev/cm³) of the active material on the active extruder. Public so reports can
    /// show the RPM a toolpath will actually command without re-deriving where flow comes from.
    /// </summary>
    public double ActiveFlowRate => SelectedPreset?.FlowRateFor(ActiveExtruderIsHf) ?? 0.463;

    private double ComputeExtrusionSpeedPercent()
    {
        float flow = (float)ActiveFlowRate;
        return KrlAnout.ComputeRpmPercent(
            (float)BeadWidth, (float)LayerHeight, (float)(PrintSpeed / 1000.0), flow);
    }

    /// <summary>Extrusion motor speed (%) written to KRL after applying <see cref="ExtrusionSpeedOffset"/>.</summary>
    public float GetEffectiveExtrusionSpeedPercent()
    {
        // Calibration override wins outright — geometry is deliberately meaningless there.
        if (ExtrusionRpmOverridePercent > 0.0)
            return (float)ExtrusionRpmOverridePercent;
        float pct = (float)ComputeExtrusionSpeedPercent() + (float)ParseSignedOffset(_extrusionSpeedOffset);
        return Math.Max(0f, pct);
    }

    private double _extrusionRpmOverridePercent;

    /// <summary>Calibration-only forced screw speed (%); 0 = off. See AppPreferences.</summary>
    public double ExtrusionRpmOverridePercent
    {
        get => _extrusionRpmOverridePercent;
        set => SetField(ref _extrusionRpmOverridePercent, value);
    }

    // -- First layer speed / RPM (override the first layer only) ---------------
    // Both are OVERRIDES: 0 = "use the calculated value". The UI shows the live
    // calculated value and lets the operator type an explicit number. Export and
    // .mass persistence use the *effective* value (override if set, else calculated).

    private bool _firstLayerAdjustmentsEnabled;
    /// <summary>Master toggle for the FIRST LAYER speed/RPM overrides. When false the first
    /// layer prints at the normal speed/RPM (export ignores the override fields) and the UI
    /// hides the input fields.</summary>
    public bool FirstLayerAdjustmentsEnabled
    {
        get => _firstLayerAdjustmentsEnabled;
        set
        {
            if (!SetField(ref _firstLayerAdjustmentsEnabled, value)) return;
            OnPropertyChanged(nameof(FirstLayerSpeedEffective));
            OnPropertyChanged(nameof(FirstLayerRpmEffective));
        }
    }

    private double _firstLayerSpeed;   // mm/s, 0 = use calculated (= normal print speed)
    /// <summary>First-layer print speed override (mm/s). 0 = use the calculated value.</summary>
    public double FirstLayerSpeed
    {
        get => _firstLayerSpeed;
        set
        {
            if (!SetField(ref _firstLayerSpeed, Math.Clamp(value, 0.0, 2000.0))) return;
            OnPropertyChanged(nameof(FirstLayerSpeedEffective));
            OnPropertyChanged(nameof(FirstLayerRpmCalculated));
            OnPropertyChanged(nameof(FirstLayerRpmEffective));
        }
    }

    private double _firstLayerRpm;     // %, 0 = use calculated
    /// <summary>First-layer extrusion RPM override (%). 0 = use the calculated value.</summary>
    public double FirstLayerRpm
    {
        get => _firstLayerRpm;
        set
        {
            if (!SetField(ref _firstLayerRpm, Math.Clamp(value, 0.0, 100.0))) return;
            OnPropertyChanged(nameof(FirstLayerRpmEffective));
        }
    }

    /// <summary>Calculated first-layer print speed (mm/s) — same as the normal print speed.</summary>
    public double FirstLayerSpeedCalculated => PrintSpeed;

    /// <summary>Effective first-layer print speed (mm/s): override if set, else calculated.</summary>
    public double FirstLayerSpeedEffective => _firstLayerSpeed > 0.0 ? _firstLayerSpeed : FirstLayerSpeedCalculated;

    /// <summary>Calculated first-layer RPM (%) — from bead width, FIRST-layer height,
    /// the effective first-layer speed, and material flow.</summary>
    public double FirstLayerRpmCalculated
    {
        get
        {
            float flow = (float)(SelectedPreset?.FlowRateFor(ActiveExtruderIsHf) ?? 0.463);
            return KrlAnout.ComputeRpmPercent(
                (float)BeadWidth, (float)FirstLayerHeight,
                (float)(FirstLayerSpeedEffective / 1000.0), flow);
        }
    }

    /// <summary>Effective first-layer RPM (%): override if set, else calculated.</summary>
    public double FirstLayerRpmEffective => _firstLayerRpm > 0.0 ? _firstLayerRpm : FirstLayerRpmCalculated;

    private double _extrusionStartWaitSec;

    /// <summary>Pause (seconds) after first RPM-on before the first extrusion move.</summary>
    public double ExtrusionStartWaitSec
    {
        get => _extrusionStartWaitSec;
        // Allow long purges for material calibration workspaces (was capped at 60 s).
        set => SetField(ref _extrusionStartWaitSec, Math.Clamp(value, 0.0, 3600.0));
    }

    private double _extrusionResumeWaitSec = 0.5;

    /// <summary>Pause (seconds) after each travel before the next extrusion move.</summary>
    public double ExtrusionResumeWaitSec
    {
        get => _extrusionResumeWaitSec;
        set
        {
            if (SetField(ref _extrusionResumeWaitSec, Math.Clamp(value, 0.0, 3600.0)))
                OnPropertyChanged(nameof(PreResumePauseMs));
        }
    }

    /// <summary>Same value as <see cref="ExtrusionResumeWaitSec"/>, in ms — the screw-on
    /// dwell after a travel before the robot moves (pressure build).</summary>
    public double PreResumePauseMs
    {
        get => _extrusionResumeWaitSec * 1000.0;
        set
        {
            if (SetField(ref _extrusionResumeWaitSec, Math.Clamp(value, 0.0, 3_600_000.0) / 1000.0))
                OnPropertyChanged(nameof(ExtrusionResumeWaitSec));
        }
    }

    private double _preTravelPauseSec = 0.5;

    /// <summary>Dwell (seconds) after the screw stops, before the travel move starts —
    /// lets barrel pressure bleed so travel entry doesn't blob.</summary>
    public double SsPreTravelWaitSec
    {
        get => _preTravelPauseSec;
        set
        {
            if (SetField(ref _preTravelPauseSec, Math.Clamp(value, 0.0, 3600.0)))
                OnPropertyChanged(nameof(PreTravelPauseMs));
        }
    }

    /// <summary>Same value as <see cref="SsPreTravelWaitSec"/>, in ms.</summary>
    public double PreTravelPauseMs
    {
        get => _preTravelPauseSec * 1000.0;
        set
        {
            if (SetField(ref _preTravelPauseSec, Math.Clamp(value, 0.0, 3_600_000.0) / 1000.0))
                OnPropertyChanged(nameof(SsPreTravelWaitSec));
        }
    }

    private double _ssResumePrimePercent = 100.0;

    /// <summary>Screw speed during the stationary resume wait, % of segment RPM (5–100).
    /// 100 = full-RPM pre-charge (legacy). Lower values widen the usable wait window —
    /// VLE reads a stopped robot as full ratio, so full-RPM priming blobs if the wait
    /// runs long. First print start always primes at 100%.</summary>
    public double SsResumePrimePercent
    {
        get => _ssResumePrimePercent;
        set => SetField(ref _ssResumePrimePercent, Math.Clamp(value, 5.0, 100.0));
    }

    private bool _digitalStartStopEnabled;

    /// <summary>
    /// Digital Start/Stop (URM): Caracol Eidos / MTruck export — <c>T1/T2/T3/RPM</c>
    /// globals, travel start/end framing, and Caracol safety header (not LFAM <c>$ANOUT</c>).
    /// </summary>
    public bool DigitalStartStopEnabled
    {
        get => _digitalStartStopEnabled;
        set
        {
            if (!SetField(ref _digitalStartStopEnabled, value)) return;
            // Keep Export-to-Robot post-process header/footer in sync so the editor and
            // export never keep an LFAM $ANOUT MAT block while URM is checked.
            ApplyUrmPostProcessTemplates(value);
            // The checkbox lives on the KRL dialog's Rules tab and proxies back to here.
            KrlPostProcess.NotifyDigitalStartStopChanged();
        }
    }

    /// <summary>
    /// Swap KRL post-process header/footer between Caracol URM and LFAM ANOUT defaults.
    /// Called when URM is toggled and after prefs/workspace load.
    /// </summary>
    public void ApplyUrmPostProcessTemplates(bool urmEnabled)
    {
        string h = KrlPostProcess.HeaderText ?? "";
        string f = KrlPostProcess.FooterText ?? "";
        bool headerIsLfamAnout = h.Contains("$ANOUT[1]", StringComparison.Ordinal)
            || (h.Contains(";FOLD MAT", StringComparison.Ordinal)
                && !h.Contains("MAT out of INI", StringComparison.Ordinal));
        bool headerIsUrm = h.Contains("CaracolSafety", StringComparison.Ordinal)
            || h.Contains("MAT out of INI", StringComparison.Ordinal);
        bool footerIsUrm = f.Contains(";AIR COMMAND", StringComparison.Ordinal)
            || f.Contains(";EXTRUDER MOTOR COMMAND", StringComparison.Ordinal);

        if (urmEnabled)
        {
            if (headerIsLfamAnout || !headerIsUrm)
                KrlPostProcess.HeaderText = KrlExporter.DefaultUrmHeaderTemplate;
            if (!footerIsUrm)
                KrlPostProcess.FooterText = KrlExporter.DefaultUrmFooterTemplate;
        }
        else
        {
            if (headerIsUrm)
                KrlPostProcess.HeaderText = KrlExporter.DefaultHeaderTemplate;
            if (footerIsUrm)
                KrlPostProcess.FooterText = KrlExporter.DefaultFooterTemplate;
        }
    }

    // -- Movement (z-hop, wipe) ------------------------------------------------

    private double _zHopMm = 3.0;

    /// <summary>Vertical lift on travel moves in mm. 0 = disabled.</summary>
    public double ZHopMm
    {
        get => _zHopMm;
        set => SetField(ref _zHopMm, Math.Max(0.0, value));
    }

    public string[] WipeModeOptions { get; } = ["Off", "Retrace", "Same-Direction"];

    private string _wipeModeDisplay = "Same-Direction";

    /// <summary>Wipe path before travel: Off, Retrace (back), or Same-Direction (forward past the point).</summary>
    public string WipeModeDisplay
    {
        get => _wipeModeDisplay;
        set => SetField(ref _wipeModeDisplay, value);
    }

    private double _wipeLengthMm = 12.0;

    /// <summary>Total wipe distance in mm.</summary>
    public double WipeLengthMm
    {
        get => _wipeLengthMm;
        set => SetField(ref _wipeLengthMm, Math.Max(0.0, value));
    }

    private double _wipeRampMm = 4.0;

    /// <summary>
    /// Wipe ramp (mm). Positive = last N mm of wipe length ramps RPM down.
    /// Negative = extra |N| mm past wipe length with ramp-down squeeze.
    /// </summary>
    public double WipeRampMm
    {
        get => _wipeRampMm;
        set => SetField(ref _wipeRampMm, Math.Clamp(value, -500.0, 500.0));
    }

    private double _wipeSpeed = 600.0;

    /// <summary>Linear speed for wipe moves in mm/s (independent of travel speed).</summary>
    public double WipeSpeed
    {
        get => _wipeSpeed;
        set => SetField(ref _wipeSpeed, Math.Clamp(value, 1.0, 2000.0));
    }

    private bool _wipeSkipShortTravels;

    /// <summary>
    /// Skip wipe before travels shorter than 2× the layer height
    /// (avoids wipe on tiny gaps between nearby beads).
    /// </summary>
    public bool WipeSkipShortTravels
    {
        get => _wipeSkipShortTravels;
        set => SetField(ref _wipeSkipShortTravels, value);
    }

    private bool _resumeRampEnabled;

    /// <summary>Stepped speed/RPM ramp after each travel before full extrusion resumes.</summary>
    public bool ResumeRampEnabled
    {
        get => _resumeRampEnabled;
        set => SetField(ref _resumeRampEnabled, value);
    }

    private double _resumeRampStartSpeed = 0.5;

    /// <summary>Print speed at the start of the post-travel ramp (mm/s).</summary>
    public double ResumeRampStartSpeed
    {
        get => _resumeRampStartSpeed;
        set => SetField(ref _resumeRampStartSpeed, Math.Clamp(value, 0.01, 2000.0));
    }

    private double _resumeRampStartRpmPercent = 1.0;

    /// <summary>Extruder motor speed at ramp start (%).</summary>
    public double ResumeRampStartRpmPercent
    {
        get => _resumeRampStartRpmPercent;
        set => SetField(ref _resumeRampStartRpmPercent, Math.Clamp(value, 0.0, 100.0));
    }

    private double _resumeRampDistanceMm = 609.6;

    /// <summary>Total ramp distance along the path (mm). Default 609.6 ≈ 2 ft.</summary>
    public double ResumeRampDistanceMm
    {
        get => _resumeRampDistanceMm;
        set => SetField(ref _resumeRampDistanceMm, Math.Clamp(value, 1.0, 10000.0));
    }

    private int _resumeRampSteps = 10;

    /// <summary>Number of discrete speed/RPM steps over the ramp distance.</summary>
    public int ResumeRampSteps
    {
        get => _resumeRampSteps;
        set => SetField(ref _resumeRampSteps, Math.Clamp(value, 1, 50));
    }

    public IReadOnlyList<string> LayerSpeedBasisOptions { get; } = ["Cut length", "Layer time"];

    private bool _layerSpeedAdaptEnabled;

    /// <summary>Scale print speed and extrusion RPM per layer between low and high rates.</summary>
    public bool LayerSpeedAdaptEnabled
    {
        get => _layerSpeedAdaptEnabled;
        set
        {
            if (!SetField(ref _layerSpeedAdaptEnabled, value)) return;
            if (!value) return;
            _layerSpeedMinMmS = PrintSpeed;
            _layerSpeedMaxMmS = PrintSpeed;
            OnPropertyChanged(nameof(LayerSpeedMinMmS));
            OnPropertyChanged(nameof(LayerSpeedMaxMmS));
        }
    }

    private string _layerSpeedBasisDisplay = "Cut length";

    /// <summary>Layer metric used for adaptive speed (display string).</summary>
    public string LayerSpeedBasisDisplay
    {
        get => _layerSpeedBasisDisplay;
        set => SetField(ref _layerSpeedBasisDisplay, value);
    }

    public LayerSpeedBasis LayerSpeedBasis => _layerSpeedBasisDisplay switch
    {
        "Layer time" => LayerSpeedBasis.LayerTime,
        _            => LayerSpeedBasis.CutLength,
    };

    private double _layerSpeedMinMmS = 10.0;

    /// <summary>Robot speed (mm/s) for the shortest/lightest layer.</summary>
    public double LayerSpeedMinMmS
    {
        get => _layerSpeedMinMmS;
        set => SetField(ref _layerSpeedMinMmS, Math.Clamp(value, 0.1, 2000.0));
    }

    private double _layerSpeedMaxMmS = 100.0;

    /// <summary>Robot speed (mm/s) for the longest/busiest layer.</summary>
    public double LayerSpeedMaxMmS
    {
        get => _layerSpeedMaxMmS;
        set => SetField(ref _layerSpeedMaxMmS, Math.Clamp(value, 0.1, 2000.0));
    }

    private bool _layerSpeedUseRpmPercent;

    /// <summary>
    /// State the range as extruder RPM percent instead of robot mm/s. The slicer then works the
    /// speed out per layer from that layer's real thickness, so a thin layer is given the extra
    /// speed it needs to reach the same flow — and the target cannot be set past the export gate.
    /// Seeds the robot ceiling from the current high speed so nothing runs faster until asked.
    /// </summary>
    public bool LayerSpeedUseRpmPercent
    {
        get => _layerSpeedUseRpmPercent;
        set
        {
            if (!SetField(ref _layerSpeedUseRpmPercent, value)) return;
            OnPropertyChanged(nameof(LayerSpeedShowMmS));
            OnPropertyChanged(nameof(LayerSpeedShowRpm));
            if (!value || _layerSpeedRobotMaxMmS > 0.1) return;
            _layerSpeedRobotMaxMmS = _layerSpeedMaxMmS;
            OnPropertyChanged(nameof(LayerSpeedRobotMaxMmS));
        }
    }

    /// <summary>Visibility helpers so only one set of range boxes shows at a time.</summary>
    public bool LayerSpeedShowMmS => !_layerSpeedUseRpmPercent;

    public bool LayerSpeedShowRpm => _layerSpeedUseRpmPercent;

    private double _layerSpeedMinRpmPercent = 40.0;

    /// <summary>Extruder RPM (%) aimed for on the shortest/lightest layer.</summary>
    public double LayerSpeedMinRpmPercent
    {
        get => _layerSpeedMinRpmPercent;
        set => SetField(ref _layerSpeedMinRpmPercent, Math.Clamp(value, 1.0, 99.0));
    }

    private double _layerSpeedMaxRpmPercent = 85.0;

    /// <summary>
    /// Extruder RPM (%) aimed for on the longest/busiest layer. Capped at 99 because that is the
    /// highest the motor can be commanded — above it the export is refused outright.
    /// </summary>
    public double LayerSpeedMaxRpmPercent
    {
        get => _layerSpeedMaxRpmPercent;
        set => SetField(ref _layerSpeedMaxRpmPercent, Math.Clamp(value, 1.0, 99.0));
    }

    private double _layerSpeedRobotMaxMmS;

    /// <summary>Ceiling on the speed an RPM target may ask for (mm/s). 0 = use the high speed box.</summary>
    public double LayerSpeedRobotMaxMmS
    {
        get => _layerSpeedRobotMaxMmS;
        set => SetField(ref _layerSpeedRobotMaxMmS, Math.Clamp(value, 0.0, 2000.0));
    }

    private static double ParseSignedOffset(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var trimmed = text.Trim();
        if (trimmed.StartsWith('+'))
            trimmed = trimmed[1..];

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;
    }

    // -- Home positions --------------------------------------------------------

    private List<(string Name, float[] Angles)> _homePositions =
        [("Default", [0f, -90f, 90f, 0f, 15f, 0f])];

    private string[] _availableHomePositionNames = ["Default"];

    /// <summary>Names of the home positions available for the active cell.</summary>
    public string[] AvailableHomePositionNames
    {
        get => _availableHomePositionNames;
        private set => SetField(ref _availableHomePositionNames, value);
    }

    private int _selectedHomePositionIndex = 0;
    private string _selectedHomePositionName = "Default";

    /// <summary>
    /// Name of the selected home position. Bound as SelectedItem (string) to avoid the
    /// Avalonia ComboBox SelectedIndex reset that occurs when ItemsSource changes.
    /// </summary>
    public string SelectedHomePositionName
    {
        get => _selectedHomePositionName;
        set
        {
            if (value is null) return;
            if (!SetField(ref _selectedHomePositionName, value)) return;
            _selectedHomePositionIndex = Math.Max(0, _homePositions.FindIndex(p => p.Name == value));
            OnHomePositionSelected?.Invoke(SelectedHomeAngles);
        }
    }

    /// <summary>Raised when the user picks a home preset — viewport applies joint angles locally.</summary>
    internal Action<float[]>? OnHomePositionSelected { get; set; }

    /// <summary>Joint angles (A1-A6, KRL degrees) for the currently selected home position.</summary>
    public float[] SelectedHomeAngles
        => _selectedHomePositionIndex < _homePositions.Count
            ? _homePositions[_selectedHomePositionIndex].Angles
            : [0f, -90f, 90f, 0f, 15f, 0f];

    /// <summary>
    /// Adds or replaces a named home position in the active list.
    /// If a position with the same name already exists it is updated in place; otherwise it is appended.
    /// </summary>
    public void AddHomePosition(string name, float[] angles)
    {
        var idx = _homePositions.FindIndex(p => p.Name == name);
        if (idx >= 0)
            _homePositions[idx] = (name, angles);
        else
            _homePositions.Add((name, angles));
        AvailableHomePositionNames = _homePositions.Select(p => p.Name).ToArray();
    }

    /// <summary>Wired by ViewportView.axaml.cs; invoked when "Set as Default" is clicked.</summary>
    internal Action? OnSetDefaultHomePositionRequested { get; set; }

    /// <summary>Saves the currently selected home position as the default for this cell.</summary>
    public RelayCommand SetDefaultHomePositionCommand { get; }

    /// <summary>
    /// Refreshes the available home position list from the given cell config and restores
    /// <paramref name="defaultPositionName"/> as the selected entry (falls back to index 0).
    /// <paramref name="userPositions"/> are appended after the cell's built-in positions.
    /// </summary>
    public void UpdateFromCell(CellConfig cell, string? defaultPositionName,
                               IReadOnlyList<HomePositionConfig>? userPositions = null)
    {
        if (userPositions is { Count: > 0 })
        {
            _homePositions = userPositions.Select(p => (p.Name, p.Angles)).ToList();
        }
        else
        {
            var positions = cell.Robot.HomePositions;
            _homePositions = positions.Count > 0
                ? positions.Select(p => (p.Name, p.Angles)).ToList()
                : [("Default", cell.Robot.HomePosition)];
        }

        AvailableHomePositionNames = _homePositions.Select(p => p.Name).ToArray();

        string nameToSelect = _homePositions.Count > 0 ? _homePositions[0].Name : "Default";
        if (defaultPositionName is not null)
        {
            int found = _homePositions.FindIndex(p => p.Name == defaultPositionName);
            if (found >= 0) nameToSelect = _homePositions[found].Name;
        }
        SelectedHomePositionName = nameToSelect;

        // Apply cell-specific toolhead orientation defaults.
        ToolheadA = cell.Robot.DefaultToolheadA;
        ToolheadB = cell.Robot.DefaultToolheadB;
        ToolheadC = cell.Robot.DefaultToolheadC;
    }
}
