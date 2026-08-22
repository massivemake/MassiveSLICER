using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using KrlExporter = MassiveSlicer.Core.IO.KrlExporter;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

public enum KrlPostProcessTab { Rules, Header, Footer }

/// <summary>ViewModel for the KRL post-processing settings dialog.</summary>
public sealed class KrlPostProcessSettingsViewModel : ViewModelBase
{
    private string _headerText = KrlExporter.DefaultHeaderTemplate;
    private string _footerText = KrlExporter.DefaultFooterTemplate;
    private string _defaultHeaderText = "";
    private string _defaultFooterText = "";
    private KrlPostProcessTab _selectedTab = KrlPostProcessTab.Rules;

    /// <summary>
    /// Owning settings VM. Rules tab Robot Mode / Travel Moves proxy to it —
    /// the flag itself lives on <see cref="AdditiveSettingsViewModel"/> because prefs,
    /// presets and the exporter all read it from there.
    /// </summary>
    public AdditiveSettingsViewModel? Owner { get; set; }

    /// <summary>Robot Mode (temps + RPM MAT), proxied from <see cref="Owner"/>.</summary>
    public bool RobotModeEnabled
    {
        get => Owner?.RobotModeEnabled ?? false;
        set
        {
            if (Owner is null || Owner.RobotModeEnabled == value) return;
            Owner.RobotModeEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Travel Moves (Start/Stop), proxied from <see cref="Owner"/>.</summary>
    public bool DigitalStartStopEnabled
    {
        get => Owner?.DigitalStartStopEnabled ?? false;
        set
        {
            if (Owner is null || Owner.DigitalStartStopEnabled == value) return;
            Owner.DigitalStartStopEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TravelStartStopEnabled));
        }
    }

    public bool TravelStartStopEnabled
    {
        get => DigitalStartStopEnabled;
        set => DigitalStartStopEnabled = value;
    }

    /// <summary>Extruder cooling air ($OUT[5]), proxied from <see cref="Owner"/>.</summary>
    public bool ExtruderAirEnabled
    {
        get => Owner?.ExtruderAirEnabled ?? false;
        set
        {
            if (Owner is null || Owner.ExtruderAirEnabled == value) return;
            Owner.ExtruderAirEnabled = value;
            OnPropertyChanged();
        }
    }

    public void NotifyExtruderAirChanged() => OnPropertyChanged(nameof(ExtruderAirEnabled));

    public void NotifyStartStopTimingChanged()
    {
        OnPropertyChanged(nameof(ExtrusionStartWaitSec));
        OnPropertyChanged(nameof(PreTravelPauseMs));
        OnPropertyChanged(nameof(PreResumePauseMs));
        OnPropertyChanged(nameof(SsResumePrimePercent));
        OnPropertyChanged(nameof(ResumeRampEnabled));
        OnPropertyChanged(nameof(ResumeRampStartSpeed));
        OnPropertyChanged(nameof(ResumeRampStartRpmPercent));
        OnPropertyChanged(nameof(ResumeRampDistanceMm));
        OnPropertyChanged(nameof(ResumeRampSteps));
    }

    public double ExtrusionStartWaitSec
    {
        get => Owner?.ExtrusionStartWaitSec ?? 0;
        set { if (Owner is null) return; Owner.ExtrusionStartWaitSec = value; OnPropertyChanged(); }
    }

    public double PreTravelPauseMs
    {
        get => Owner?.PreTravelPauseMs ?? 500;
        set { if (Owner is null) return; Owner.PreTravelPauseMs = value; OnPropertyChanged(); }
    }

    public double PreResumePauseMs
    {
        get => Owner?.PreResumePauseMs ?? 0;
        set { if (Owner is null) return; Owner.PreResumePauseMs = value; OnPropertyChanged(); }
    }

    public double SsResumePrimePercent
    {
        get => Owner?.SsResumePrimePercent ?? 100;
        set { if (Owner is null) return; Owner.SsResumePrimePercent = value; OnPropertyChanged(); }
    }

    public bool ResumeRampEnabled
    {
        get => Owner?.ResumeRampEnabled ?? false;
        set { if (Owner is null) return; Owner.ResumeRampEnabled = value; OnPropertyChanged(); }
    }

    public double ResumeRampStartSpeed
    {
        get => Owner?.ResumeRampStartSpeed ?? 0.5;
        set { if (Owner is null) return; Owner.ResumeRampStartSpeed = value; OnPropertyChanged(); }
    }

    public double ResumeRampStartRpmPercent
    {
        get => Owner?.ResumeRampStartRpmPercent ?? 1;
        set { if (Owner is null) return; Owner.ResumeRampStartRpmPercent = value; OnPropertyChanged(); }
    }

    public double ResumeRampDistanceMm
    {
        get => Owner?.ResumeRampDistanceMm ?? 609.6;
        set { if (Owner is null) return; Owner.ResumeRampDistanceMm = value; OnPropertyChanged(); }
    }

    public int ResumeRampSteps
    {
        get => Owner?.ResumeRampSteps ?? 10;
        set { if (Owner is null) return; Owner.ResumeRampSteps = value; OnPropertyChanged(); }
    }

    public void NotifyDigitalStartStopChanged()
    {
        OnPropertyChanged(nameof(DigitalStartStopEnabled));
        OnPropertyChanged(nameof(TravelStartStopEnabled));
    }

    public void NotifyCodeEditorInjectChanged()
    {
        OnPropertyChanged(nameof(CodeEditorShortTravelMm));
        OnPropertyChanged(nameof(CodeEditorPrintSpeedHint));
        OnPropertyChanged(nameof(CodeEditorStartExtrudingCommand));
        OnPropertyChanged(nameof(CodeEditorStopExtrudingCommand));
        OnPropertyChanged(nameof(CodeEditorStopDistance));
        OnPropertyChanged(nameof(CodeEditorStopUnits));
        OnPropertyChanged(nameof(CodeEditorStopDirection));
        OnPropertyChanged(nameof(CodeEditorEnterUrmCommand));
        OnPropertyChanged(nameof(CodeEditorExitUrmCommand));
        OnPropertyChanged(nameof(CodeEditorEnterUrmDistance));
        OnPropertyChanged(nameof(CodeEditorEnterUrmUnits));
        OnPropertyChanged(nameof(CodeEditorEnterUrmDirection));
        OnPropertyChanged(nameof(CodeEditorExitUrmDistance));
        OnPropertyChanged(nameof(CodeEditorExitUrmUnits));
        OnPropertyChanged(nameof(CodeEditorExitUrmDirection));
        OnPropertyChanged(nameof(CodeEditorAlwaysInsert));
        OnPropertyChanged(nameof(CodeEditorPointLoaderSafeIo));
    }

    public string[] CodeEditorUnitOptions => Owner?.CodeEditorUnitOptions ?? CodeEditorInjectSettings.UnitOptions;
    public string[] CodeEditorDirectionOptions => Owner?.CodeEditorDirectionOptions ?? CodeEditorInjectSettings.DirectionOptions;

    public string CodeEditorPrintSpeedHint
        => Owner?.CodeEditorPrintSpeedHint
           ?? "Time offsets use print speed. Stop $VEL.CP is half of that.";

    public double CodeEditorSpeedMmS
    {
        get => Owner?.CodeEditorSpeedMmS ?? 0;
        set { if (Owner is null) return; Owner.CodeEditorSpeedMmS = value; OnPropertyChanged(); }
    }

    public double CodeEditorShortTravelMm
    {
        get => Owner?.CodeEditorShortTravelMm ?? 1;
        set { if (Owner is null) return; Owner.CodeEditorShortTravelMm = value; OnPropertyChanged(); }
    }

    public string CodeEditorStartExtrudingCommand
    {
        get => Owner?.CodeEditorStartExtrudingCommand ?? "";
        set { if (Owner is null) return; Owner.CodeEditorStartExtrudingCommand = value; OnPropertyChanged(); }
    }

    public string CodeEditorStopExtrudingCommand
    {
        get => Owner?.CodeEditorStopExtrudingCommand ?? "";
        set { if (Owner is null) return; Owner.CodeEditorStopExtrudingCommand = value; OnPropertyChanged(); }
    }

    public double CodeEditorStopDistance
    {
        get => Owner?.CodeEditorStopDistance ?? 350;
        set { if (Owner is null) return; Owner.CodeEditorStopDistance = value; OnPropertyChanged(); }
    }

    public string CodeEditorStopUnits
    {
        get => Owner?.CodeEditorStopUnits ?? "Milliseconds";
        set { if (Owner is null) return; Owner.CodeEditorStopUnits = value; OnPropertyChanged(); }
    }

    public string CodeEditorStopDirection
    {
        get => Owner?.CodeEditorStopDirection ?? "Before";
        set { if (Owner is null) return; Owner.CodeEditorStopDirection = value; OnPropertyChanged(); }
    }

    public string CodeEditorEnterUrmCommand
    {
        get => Owner?.CodeEditorEnterUrmCommand ?? "";
        set { if (Owner is null) return; Owner.CodeEditorEnterUrmCommand = value; OnPropertyChanged(); }
    }

    public string CodeEditorExitUrmCommand
    {
        get => Owner?.CodeEditorExitUrmCommand ?? "";
        set { if (Owner is null) return; Owner.CodeEditorExitUrmCommand = value; OnPropertyChanged(); }
    }

    public double CodeEditorEnterUrmDistance
    {
        get => Owner?.CodeEditorEnterUrmDistance ?? 3500;
        set { if (Owner is null) return; Owner.CodeEditorEnterUrmDistance = value; OnPropertyChanged(); }
    }

    public string CodeEditorEnterUrmUnits
    {
        get => Owner?.CodeEditorEnterUrmUnits ?? "Milliseconds";
        set { if (Owner is null) return; Owner.CodeEditorEnterUrmUnits = value; OnPropertyChanged(); }
    }

    public string CodeEditorEnterUrmDirection
    {
        get => Owner?.CodeEditorEnterUrmDirection ?? "Before";
        set { if (Owner is null) return; Owner.CodeEditorEnterUrmDirection = value; OnPropertyChanged(); }
    }

    public double CodeEditorExitUrmDistance
    {
        get => Owner?.CodeEditorExitUrmDistance ?? 3500;
        set { if (Owner is null) return; Owner.CodeEditorExitUrmDistance = value; OnPropertyChanged(); }
    }

    public string CodeEditorExitUrmUnits
    {
        get => Owner?.CodeEditorExitUrmUnits ?? "Milliseconds";
        set { if (Owner is null) return; Owner.CodeEditorExitUrmUnits = value; OnPropertyChanged(); }
    }

    public string CodeEditorExitUrmDirection
    {
        get => Owner?.CodeEditorExitUrmDirection ?? "After";
        set { if (Owner is null) return; Owner.CodeEditorExitUrmDirection = value; OnPropertyChanged(); }
    }

    public bool CodeEditorAlwaysInsert
    {
        get => Owner?.CodeEditorAlwaysInsert ?? true;
        set { if (Owner is null) return; Owner.CodeEditorAlwaysInsert = value; OnPropertyChanged(); }
    }

    public bool CodeEditorPointLoaderSafeIo
    {
        get => Owner?.CodeEditorPointLoaderSafeIo ?? true;
        set { if (Owner is null) return; Owner.CodeEditorPointLoaderSafeIo = value; OnPropertyChanged(); }
    }

    public void NotifyRobotModeChanged() => OnPropertyChanged(nameof(RobotModeEnabled));

    public void NotifyOrientationSmoothingChanged()
    {
        OnPropertyChanged(nameof(SmoothRotation));
        OnPropertyChanged(nameof(ShowSmoothRotationRadius));
        OnPropertyChanged(nameof(SmoothRotationRadius));
        OnPropertyChanged(nameof(SmoothRotationMaxRateDegPerMm));
        OnPropertyChanged(nameof(OrientationLookAheadMm));
        OnPropertyChanged(nameof(OrientationSigmaMm));
    }

    public bool SmoothRotation
    {
        get => Owner?.SmoothRotation ?? false;
        set
        {
            if (Owner is null || Owner.SmoothRotation == value) return;
            Owner.SmoothRotation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSmoothRotationRadius));
        }
    }

    public bool ShowSmoothRotationRadius => SmoothRotation;

    public int SmoothRotationRadius
    {
        get => Owner?.SmoothRotationRadius ?? 5;
        set { if (Owner is null) return; Owner.SmoothRotationRadius = value; OnPropertyChanged(); }
    }

    public double SmoothRotationMaxRateDegPerMm
    {
        get => Owner?.SmoothRotationMaxRateDegPerMm ?? 0;
        set { if (Owner is null) return; Owner.SmoothRotationMaxRateDegPerMm = value; OnPropertyChanged(); }
    }

    public double OrientationLookAheadMm
    {
        get => Owner?.OrientationLookAheadMm ?? 0;
        set { if (Owner is null) return; Owner.OrientationLookAheadMm = value; OnPropertyChanged(); }
    }

    public double OrientationSigmaMm
    {
        get => Owner?.OrientationSigmaMm ?? 30;
        set { if (Owner is null) return; Owner.OrientationSigmaMm = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// KUKA <c>$APO.CVEL</c> (0–100), proxied from <see cref="Owner"/> for the same reason as
    /// the URM flag: prefs, presets and the exporter all read it off
    /// <see cref="AdditiveSettingsViewModel"/>.
    /// </summary>
    public double ApoCvel
    {
        get => Owner?.ApoCvel ?? 0.0;
        set
        {
            if (Owner is null) return;
            Owner.ApoCvel = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Re-reads the proxied $APO.CVEL after the owner changes it (preset or prefs load).</summary>
    public void NotifyApoCvelChanged() => OnPropertyChanged(nameof(ApoCvel));

    public string HeaderText
    {
        get => _headerText;
        set => SetField(ref _headerText, value);
    }

    public string FooterText
    {
        get => _footerText;
        set => SetField(ref _footerText, value);
    }

    public KrlPostProcessTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetField(ref _selectedTab, value)) return;
            OnPropertyChanged(nameof(IsRulesTab));
            OnPropertyChanged(nameof(IsHeaderTab));
            OnPropertyChanged(nameof(IsFooterTab));
        }
    }

    public bool IsRulesTab  => _selectedTab == KrlPostProcessTab.Rules;
    public bool IsHeaderTab => _selectedTab == KrlPostProcessTab.Header;
    public bool IsFooterTab => _selectedTab == KrlPostProcessTab.Footer;

    public RelayCommand ShowRulesTabCommand  { get; }
    public RelayCommand ShowHeaderTabCommand { get; }
    public RelayCommand ShowFooterTabCommand { get; }
    public RelayCommand ResetHeaderCommand { get; }
    public RelayCommand ResetFooterCommand { get; }
    public RelayCommand SaveHeaderDefaultCommand { get; }
    public RelayCommand SaveFooterDefaultCommand { get; }

    public KrlPostProcessSettingsViewModel()
    {
        ShowRulesTabCommand  = new RelayCommand(() => SelectedTab = KrlPostProcessTab.Rules);
        ShowHeaderTabCommand = new RelayCommand(() => SelectedTab = KrlPostProcessTab.Header);
        ShowFooterTabCommand = new RelayCommand(() => SelectedTab = KrlPostProcessTab.Footer);
        // Reset restores the operator's saved default when there is one, else the built-in
        // LFAM template. URM mode re-applies Caracol templates from AdditiveSettings.
        ResetHeaderCommand   = new RelayCommand(() => HeaderText = EffectiveDefaultHeader);
        ResetFooterCommand   = new RelayCommand(() => FooterText = EffectiveDefaultFooter);
        SaveHeaderDefaultCommand = new RelayCommand(SaveHeaderAsDefault);
        SaveFooterDefaultCommand = new RelayCommand(SaveFooterAsDefault);
    }

    /// <summary>What "Reset to default" restores: the saved default, else the built-in template.</summary>
    private string EffectiveDefaultHeader => string.IsNullOrWhiteSpace(_defaultHeaderText)
        ? KrlExporter.DefaultHeaderTemplate
        : _defaultHeaderText;

    private string EffectiveDefaultFooter => string.IsNullOrWhiteSpace(_defaultFooterText)
        ? KrlExporter.DefaultFooterTemplate
        : _defaultFooterText;

    public bool HasSavedHeaderDefault => !string.IsNullOrWhiteSpace(_defaultHeaderText);
    public bool HasSavedFooterDefault => !string.IsNullOrWhiteSpace(_defaultFooterText);

    public string HeaderDefaultStatus => HasSavedHeaderDefault
        ? "Reset restores your saved default."
        : "Reset restores the built-in template.";

    public string FooterDefaultStatus => HasSavedFooterDefault
        ? "Reset restores your saved default."
        : "Reset restores the built-in template.";

    /// <summary>Stores the header currently in the editor as the new "Reset to default" target.</summary>
    private void SaveHeaderAsDefault()
    {
        _defaultHeaderText = HeaderText;
        OnPropertyChanged(nameof(HasSavedHeaderDefault));
        OnPropertyChanged(nameof(HeaderDefaultStatus));
        Save();
    }

    private void SaveFooterAsDefault()
    {
        _defaultFooterText = FooterText;
        OnPropertyChanged(nameof(HasSavedFooterDefault));
        OnPropertyChanged(nameof(FooterDefaultStatus));
        Save();
    }

    /// <summary>True when the current header is the LFAM <c>$ANOUT</c> MAT style.</summary>
    public bool HeaderLooksLikeLfamAnout =>
        HeaderText.Contains("$ANOUT[1]", StringComparison.Ordinal)
        || (HeaderText.Contains(";FOLD MAT", StringComparison.Ordinal)
            && !HeaderText.Contains("MAT out of INI", StringComparison.Ordinal));

    public void LoadFrom(KrlPostProcessSettings s)
    {
        _defaultHeaderText = s.DefaultHeaderText ?? "";
        _defaultFooterText = s.DefaultFooterText ?? "";
        OnPropertyChanged(nameof(HasSavedHeaderDefault));
        OnPropertyChanged(nameof(HasSavedFooterDefault));
        OnPropertyChanged(nameof(HeaderDefaultStatus));
        OnPropertyChanged(nameof(FooterDefaultStatus));

        HeaderText = string.IsNullOrWhiteSpace(s.HeaderText)
            ? EffectiveDefaultHeader
            : s.HeaderText;
        FooterText = string.IsNullOrWhiteSpace(s.FooterText)
            ? EffectiveDefaultFooter
            : s.FooterText;

        ApplyRulesToOwner(s);
    }

    /// <summary>Overlay factory Rules onto the owning Additive VM (null fields stay as-is).</summary>
    public void ApplyRulesToOwner(KrlPostProcessSettings s)
    {
        if (Owner is null || (!s.RulesSaved && s.CodeEditorInject is null))
            return;

        if (s.RobotModeEnabled is { } robot)
            Owner.RobotModeEnabled = robot;
        if (s.TravelStartStopEnabled is { } travel)
            Owner.TravelStartStopEnabled = travel;
        if (s.ExtruderAirEnabled is { } air)
            Owner.ExtruderAirEnabled = air;
        if (s.ApoCvel is { } cvel)
            Owner.ApoCvel = cvel;
        if (s.CodeEditorInject is { } inject)
            Owner.CodeEditorInject = inject.Clone();

        if (s.SmoothRotation is { } smooth)
            Owner.SmoothRotation = smooth;
        if (s.SmoothRotationRadius is { } radius)
            Owner.SmoothRotationRadius = radius;
        if (s.SmoothRotationMaxRateDegPerMm is { } rate)
            Owner.SmoothRotationMaxRateDegPerMm = rate;
        if (s.OrientationLookAheadMm is { } look)
            Owner.OrientationLookAheadMm = look;
        if (s.OrientationSigmaMm is { } sigma)
            Owner.OrientationSigmaMm = sigma;

        if (s.ExtrusionStartWaitSec is { } startWait)
            Owner.ExtrusionStartWaitSec = startWait;
        if (s.ExtrusionResumeWaitSec is { } resumeWait)
            Owner.ExtrusionResumeWaitSec = resumeWait;
        if (s.SsPreTravelWaitSec is { } preTravel)
            Owner.SsPreTravelWaitSec = preTravel;
        if (s.SsResumePrimePercent is { } prime)
            Owner.SsResumePrimePercent = prime;

        if (s.ResumeRampEnabled is { } rampOn)
            Owner.ResumeRampEnabled = rampOn;
        if (s.ResumeRampStartSpeed is { } rampSpeed)
            Owner.ResumeRampStartSpeed = rampSpeed;
        if (s.ResumeRampStartRpmPercent is { } rampRpm)
            Owner.ResumeRampStartRpmPercent = rampRpm;
        if (s.ResumeRampDistanceMm is { } rampDist)
            Owner.ResumeRampDistanceMm = rampDist;
        if (s.ResumeRampSteps is { } rampSteps)
            Owner.ResumeRampSteps = rampSteps;

        NotifyDigitalStartStopChanged();
        NotifyRobotModeChanged();
        NotifyExtruderAirChanged();
        NotifyApoCvelChanged();
        NotifyOrientationSmoothingChanged();
        NotifyStartStopTimingChanged();
        NotifyCodeEditorInjectChanged();
    }

    public KrlPostProcessSettings ToSettings()
    {
        var add = Owner;
        return new()
        {
            HeaderText        = HeaderText,
            FooterText        = FooterText,
            DefaultHeaderText = _defaultHeaderText,
            DefaultFooterText = _defaultFooterText,
            RulesSaved        = true,
            RobotModeEnabled  = add?.RobotModeEnabled,
            TravelStartStopEnabled = add?.TravelStartStopEnabled,
            ExtruderAirEnabled = add?.ExtruderAirEnabled,
            ApoCvel           = add?.ApoCvel,
            CodeEditorInject  = add?.CodeEditorInject.Clone(),
            SmoothRotation    = add?.SmoothRotation,
            SmoothRotationRadius = add?.SmoothRotationRadius,
            SmoothRotationMaxRateDegPerMm = add?.SmoothRotationMaxRateDegPerMm,
            OrientationLookAheadMm = add?.OrientationLookAheadMm,
            OrientationSigmaMm = add?.OrientationSigmaMm,
            ExtrusionStartWaitSec = add?.ExtrusionStartWaitSec,
            ExtrusionResumeWaitSec = add?.ExtrusionResumeWaitSec,
            SsPreTravelWaitSec = add?.SsPreTravelWaitSec,
            SsResumePrimePercent = add?.SsResumePrimePercent,
            ResumeRampEnabled = add?.ResumeRampEnabled,
            ResumeRampStartSpeed = add?.ResumeRampStartSpeed,
            ResumeRampStartRpmPercent = add?.ResumeRampStartRpmPercent,
            ResumeRampDistanceMm = add?.ResumeRampDistanceMm,
            ResumeRampSteps = add?.ResumeRampSteps,
        };
    }

    public void Save() => KrlPostProcessLoader.Save(ToSettings());
}