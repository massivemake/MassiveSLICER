using System;
using MassiveSlicer.Commands;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Parameters for subtractive (relief milling) toolpaths: tool + cutting params, the relief
/// heightmap and its placement, and the editable KRL post-processor header/footer templates.
/// Spindle on/RPM control lives in the templates (KUKA 0-10V → ATV340 VFD), not hardcoded.
/// </summary>
public sealed class SubtractiveSettingsViewModel : ViewModelBase
{
    public SubtractiveSettingsViewModel()
    {
        BrowseHeightmapCommand = new RelayCommand(() => BrowseHeightmapRequested?.Invoke(this, EventArgs.Empty));
    }

    // -- Tool & cutting --------------------------------------------------------

    private double _toolDiameterMm = 6;
    private bool   _ballEnd = true;
    private double _stepoverMm = 3;
    private double _stepdownMm = 2;
    private double _finishAllowanceMm = 0.3;
    private double _feedRateMmMin = 3000;
    private double _plungeFeedMmMin = 1000;
    private double _rapidZMm = 50;
    private double _spindleRpm = 12000;
    private double _maxDepthMm = 0;   // 0 = unlimited

    /// <summary>Cutter diameter (mm); used for the anti-gouge inverse offset.</summary>
    public double ToolDiameterMm { get => _toolDiameterMm; set => SetField(ref _toolDiameterMm, value); }
    /// <summary>True = ball-nose (rounded), false = flat end-mill.</summary>
    public bool   BallEnd        { get => _ballEnd; set => SetField(ref _ballEnd, value); }
    /// <summary>Finish raster spacing (mm) — sets toolpath fidelity, not image resolution.</summary>
    public double StepoverMm     { get => _stepoverMm; set => SetField(ref _stepoverMm, value); }
    /// <summary>Axial stepdown per roughing pass (mm).</summary>
    public double StepdownMm     { get => _stepdownMm; set => SetField(ref _stepdownMm, value); }
    /// <summary>Stock left above the final surface during roughing (mm).</summary>
    public double FinishAllowanceMm { get => _finishAllowanceMm; set => SetField(ref _finishAllowanceMm, value); }
    /// <summary>Cutting feed (mm/min).</summary>
    public double FeedRateMmMin  { get => _feedRateMmMin; set => SetField(ref _feedRateMmMin, value); }
    /// <summary>Plunge feed (mm/min).</summary>
    public double PlungeFeedMmMin { get => _plungeFeedMmMin; set => SetField(ref _plungeFeedMmMin, value); }
    /// <summary>Safe retract height above the reference plane for rapids (mm).</summary>
    public double RapidZMm       { get => _rapidZMm; set => SetField(ref _rapidZMm, value); }
    /// <summary>Spindle RPM (informational / for the header template).</summary>
    public double SpindleRpm     { get => _spindleRpm; set => SetField(ref _spindleRpm, value); }
    /// <summary>Hard depth limit below the reference plane (mm); 0 = unlimited.</summary>
    public double MaxDepthMm     { get => _maxDepthMm; set => SetField(ref _maxDepthMm, value); }

    // -- Relief heightmap ------------------------------------------------------

    private string _heightmapPath = string.Empty;
    private double _heightScaleMm = 5;
    private bool   _invertHeightmap;
    private bool   _autoReferenceFromTop = true;
    private double _referencePlaneZ;
    private bool   _autoFootprint = true;
    private double _footprintOriginX, _footprintOriginY, _footprintWidthMm = 100, _footprintLengthMm = 100;

    /// <summary>Path to the grayscale relief image (PNG/JPG). White = high surface.</summary>
    public string HeightmapPath  { get => _heightmapPath; set => SetField(ref _heightmapPath, value); }
    /// <summary>Relief depth between black and white (mm).</summary>
    public double HeightScaleMm  { get => _heightScaleMm; set => SetField(ref _heightScaleMm, value); }
    /// <summary>Flip black/white.</summary>
    public bool   InvertHeightmap { get => _invertHeightmap; set => SetField(ref _invertHeightmap, value); }

    // -- Displaced surface (PBR maps -> geometry) ------------------------------

    private double _displacementDistanceMm = 3;

    /// <summary>
    /// How far the detail map pushes the low-poly surface along its normal (mm). The map's
    /// source is the supplied displacement/bump/height image (<see cref="HeightmapPath"/>) if set,
    /// otherwise the model's embedded normal map integrated to height.
    /// </summary>
    public double DisplacementDistanceMm { get => _displacementDistanceMm; set => SetField(ref _displacementDistanceMm, value); }

    private double _analysisToleranceMm = 0.1;
    private string _millAnalysisText = string.Empty;

    /// <summary>Tolerance band (mm) for the gouge/residual fail-rate analysis after a multi-axis pass.</summary>
    public double AnalysisToleranceMm { get => _analysisToleranceMm; set => SetField(ref _analysisToleranceMm, value); }

    /// <summary>Human-readable result of the last multi-axis surface deviation analysis (shown in the panel).</summary>
    public string MillAnalysisText { get => _millAnalysisText; set => SetField(ref _millAnalysisText, value); }

    /// <summary>Use the selected part's top-face Z as the reference plane.</summary>
    public bool   AutoReferenceFromTop { get => _autoReferenceFromTop; set => SetField(ref _autoReferenceFromTop, value); }
    /// <summary>Manual reference plane Z (world mm) when <see cref="AutoReferenceFromTop"/> is false.</summary>
    public double ReferencePlaneZ { get => _referencePlaneZ; set => SetField(ref _referencePlaneZ, value); }

    /// <summary>Map the relief onto the selected part's XY bounding box.</summary>
    public bool   AutoFootprint  { get => _autoFootprint; set => SetField(ref _autoFootprint, value); }
    public double FootprintOriginX { get => _footprintOriginX; set => SetField(ref _footprintOriginX, value); }
    public double FootprintOriginY { get => _footprintOriginY; set => SetField(ref _footprintOriginY, value); }
    public double FootprintWidthMm { get => _footprintWidthMm; set => SetField(ref _footprintWidthMm, value); }
    public double FootprintLengthMm { get => _footprintLengthMm; set => SetField(ref _footprintLengthMm, value); }

    /// <summary>Opens the heightmap file picker (handled by the window).</summary>
    public RelayCommand BrowseHeightmapCommand { get; }
    public event EventHandler? BrowseHeightmapRequested;

    // -- KRL post-processor templates ------------------------------------------

    private string _headerTemplate = string.Empty;
    private string _footerTemplate = string.Empty;

    /// <summary>Editable KRL program header (spindle on/RPM lives here). Supports {PROGNAME}, {TOOL_NO}, {DATE}, etc.</summary>
    public string HeaderTemplate { get => _headerTemplate; set => SetField(ref _headerTemplate, value); }
    /// <summary>Editable KRL program footer (spindle off lives here).</summary>
    public string FooterTemplate { get => _footerTemplate; set => SetField(ref _footerTemplate, value); }
}
