using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Parameters for subtractive (milling/routing) slicing, including the
/// editable KRL post-processor header and footer templates.
/// </summary>
public sealed class SubtractiveSettingsViewModel : ViewModelBase
{
    private double _feedRateMmMin = 3000;
    private double _stepdownMm = 2;
    private string _headerTemplate = string.Empty;

    /// <summary>Spindle feed rate for mill toolpaths (mm/min).</summary>
    public double FeedRateMmMin
    {
        get => _feedRateMmMin;
        set => SetField(ref _feedRateMmMin, value);
    }

    /// <summary>Axial stepdown per mill pass (mm).</summary>
    public double StepdownMm
    {
        get => _stepdownMm;
        set => SetField(ref _stepdownMm, value);
    }

    /// <summary>
    /// Editable KRL program header. Supports template variables such as
    /// <c>{PROGNAME}</c>, <c>{TOOL_NO}</c>, <c>{DATE}</c>, etc.
    /// </summary>
    public string HeaderTemplate
    {
        get => _headerTemplate;
        set => SetField(ref _headerTemplate, value);
    }

    private string _footerTemplate = string.Empty;

    /// <summary>
    /// Editable KRL program footer. Applied after all generated move statements.
    /// </summary>
    public string FooterTemplate
    {
        get => _footerTemplate;
        set => SetField(ref _footerTemplate, value);
    }
}
