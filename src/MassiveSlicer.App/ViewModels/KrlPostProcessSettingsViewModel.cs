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
    private bool _travelSetAnout4Zero = true;
    private string _headerText = KrlExporter.DefaultHeaderTemplate;
    private string _footerText = KrlExporter.DefaultFooterTemplate;
    private KrlPostProcessTab _selectedTab = KrlPostProcessTab.Rules;

    public bool TravelSetAnout4Zero
    {
        get => _travelSetAnout4Zero;
        set => SetField(ref _travelSetAnout4Zero, value);
    }

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

    public KrlPostProcessSettingsViewModel()
    {
        ShowRulesTabCommand  = new RelayCommand(() => SelectedTab = KrlPostProcessTab.Rules);
        ShowHeaderTabCommand = new RelayCommand(() => SelectedTab = KrlPostProcessTab.Header);
        ShowFooterTabCommand = new RelayCommand(() => SelectedTab = KrlPostProcessTab.Footer);
        ResetHeaderCommand   = new RelayCommand(() => HeaderText = KrlExporter.DefaultHeaderTemplate);
        ResetFooterCommand   = new RelayCommand(() => FooterText = KrlExporter.DefaultFooterTemplate);
    }

    public void LoadFrom(KrlPostProcessSettings s)
    {
        TravelSetAnout4Zero = s.TravelSetAnout4Zero;
        HeaderText = string.IsNullOrWhiteSpace(s.HeaderText)
            ? KrlExporter.DefaultHeaderTemplate
            : s.HeaderText;
        FooterText = string.IsNullOrWhiteSpace(s.FooterText)
            ? KrlExporter.DefaultFooterTemplate
            : s.FooterText;
    }

    public KrlPostProcessSettings ToSettings() => new()
    {
        TravelSetAnout4Zero = TravelSetAnout4Zero,
        HeaderText          = HeaderText,
        FooterText          = FooterText,
    };

    public void Save() => KrlPostProcessLoader.Save(ToSettings());
}