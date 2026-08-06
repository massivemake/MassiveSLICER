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
    /// Owning settings VM. The Rules tab's Digital Start/Stop checkbox proxies to it —
    /// the flag itself lives on <see cref="AdditiveSettingsViewModel"/> because prefs,
    /// presets and the exporter all read it from there.
    /// </summary>
    public AdditiveSettingsViewModel? Owner { get; set; }

    /// <summary>Digital Start/Stop (URM), proxied from <see cref="Owner"/>.</summary>
    public bool DigitalStartStopEnabled
    {
        get => Owner?.DigitalStartStopEnabled ?? false;
        set
        {
            if (Owner is null || Owner.DigitalStartStopEnabled == value) return;
            Owner.DigitalStartStopEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Re-reads the proxied URM flag after the owner changes it (preset or prefs load).</summary>
    public void NotifyDigitalStartStopChanged() => OnPropertyChanged(nameof(DigitalStartStopEnabled));

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
    }

    public KrlPostProcessSettings ToSettings() => new()
    {
        HeaderText        = HeaderText,
        FooterText        = FooterText,
        DefaultHeaderText = _defaultHeaderText,
        DefaultFooterText = _defaultFooterText,
    };

    public void Save() => KrlPostProcessLoader.Save(ToSettings());
}