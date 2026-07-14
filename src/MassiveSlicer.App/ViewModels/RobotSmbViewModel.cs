using Avalonia.Threading;
using MassiveSlicer.App;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Per-cell SMB credentials editor (ROBOT NETWORK section). Shows and edits the
/// <see cref="RobotSmbConfig"/> for the active cell; fields write through to
/// prefs.json immediately. Passwords never leave the local machine — the
/// workspace snapshot scrubs them.
/// </summary>
public sealed class RobotSmbViewModel : ViewModelBase
{
    private AppPreferences? _prefs;
    private Action? _savePrefs;
    private Action<string>? _log;
    private RobotSmbConfig? _config;
    private bool _loading;

    public void Initialize(AppPreferences prefs, Action savePrefs, Action<string> log)
    {
        _prefs = prefs;
        _savePrefs = savePrefs;
        _log = log;
    }

    /// <summary>Called on every cell swap (possibly off the UI thread); loads (or creates)
    /// that cell's config. <paramref name="defaultHost"/> pre-fills the robot/bridge IP.</summary>
    public void SetActiveCell(string cellName, string? defaultHost)
        => Post(() => SetActiveCellCore(cellName, defaultHost));

    private void SetActiveCellCore(string cellName, string? defaultHost)
    {
        if (_prefs is null || string.IsNullOrWhiteSpace(cellName)) return;

        _config = _prefs.RobotSmb.FirstOrDefault(c =>
            string.Equals(c.CellName, cellName, StringComparison.OrdinalIgnoreCase));
        if (_config is null)
        {
            _config = new RobotSmbConfig { CellName = cellName, Host = defaultHost ?? "" };
            _prefs.RobotSmb.Add(_config);
        }
        else if (_config.Host.Length == 0 && defaultHost is { Length: > 0 })
        {
            _config.Host = defaultHost;
        }

        _loading = true;
        try
        {
            CellName = cellName;
            OnPropertyChanged(nameof(Host));
            OnPropertyChanged(nameof(Share));
            OnPropertyChanged(nameof(Folder));
            OnPropertyChanged(nameof(Username));
            OnPropertyChanged(nameof(Password));
        }
        finally
        {
            _loading = false;
        }
        Status = "";
    }

    /// <summary>The active cell's config for the export flow (null when never configured).</summary>
    public RobotSmbConfig? ActiveConfig => _config;

    public bool IsConfigured =>
        _config is { } c && c.Host.Trim().Length > 0 && c.Username.Trim().Length > 0;

    private string _cellName = "";
    public string CellName
    {
        get => _cellName;
        private set => SetField(ref _cellName, value);
    }

    public string Host
    {
        get => _config?.Host ?? "";
        set { if (_config is { } c && c.Host != value) { c.Host = value.Trim(); Persist(); } }
    }

    public string Share
    {
        get => _config?.Share ?? "d";
        set { if (_config is { } c && c.Share != value) { c.Share = value.Trim(); Persist(); } }
    }

    public string Folder
    {
        get => _config?.Folder ?? "";
        set { if (_config is { } c && c.Folder != value) { c.Folder = value.Trim(); Persist(); } }
    }


    public string Username
    {
        get => _config?.Username ?? "";
        set { if (_config is { } c && c.Username != value) { c.Username = value.Trim(); Persist(); } }
    }

    public string Password
    {
        get => _config?.Password ?? "";
        set { if (_config is { } c && (c.Password ?? "") != value) { c.Password = value; Persist(); } }
    }

    private string _status = "";
    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    private bool _testing;
    public RelayCommand TestCommand => _testCommand ??= new RelayCommand(
        () => _ = TestAsync(),
        () => !_testing && IsConfigured);
    private RelayCommand? _testCommand;

    private async Task TestAsync()
    {
        if (_config is not { } cfg) return;
        _testing = true;
        TestCommand.RaiseCanExecuteChanged();
        Status = $"Testing \\\\{cfg.Host}\\{cfg.Share}…";
        try
        {
            var (ok, message) = await Task.Run(() => RobotSmbUploader.Test(cfg));
            Post(() =>
            {
                Status = ok ? message : $"Failed — {message}";
                _log?.Invoke($"[smb] {cfg.CellName}: {(ok ? "OK" : "FAIL")} — {message}");
            });
        }
        finally
        {
            _testing = false;
            Post(() => TestCommand.RaiseCanExecuteChanged());
        }
    }

    private void Persist()
    {
        if (_loading) return;
        _savePrefs?.Invoke();
        OnPropertyChanged(nameof(IsConfigured));
        TestCommand.RaiseCanExecuteChanged();
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

/// <summary>
/// One cell's SMB config row in Preferences → Connections. Same write-through
/// semantics as <see cref="RobotSmbViewModel"/> but bound to a fixed config.
/// </summary>
public sealed class RobotSmbRowViewModel : ViewModelBase
{
    private readonly RobotSmbConfig _cfg;
    private readonly Action _save;
    private bool _testing;

    public RobotSmbRowViewModel(RobotSmbConfig cfg, Action save)
    {
        _cfg = cfg;
        _save = save;
    }

    public string CellName => _cfg.CellName;

    public string Host
    {
        get => _cfg.Host;
        set { if (_cfg.Host != value) { _cfg.Host = value.Trim(); _save(); Refresh(); } }
    }

    public string Share
    {
        get => _cfg.Share;
        set { if (_cfg.Share != value) { _cfg.Share = value.Trim(); _save(); } }
    }

    public string Folder
    {
        get => _cfg.Folder;
        set { if (_cfg.Folder != value) { _cfg.Folder = value.Trim(); _save(); } }
    }

    public string Username
    {
        get => _cfg.Username;
        set { if (_cfg.Username != value) { _cfg.Username = value.Trim(); _save(); Refresh(); } }
    }

    public string Password
    {
        get => _cfg.Password ?? "";
        set { if ((_cfg.Password ?? "") != value) { _cfg.Password = value; _save(); } }
    }

    private string _status = "";
    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public RelayCommand TestCommand => _testCommand ??= new RelayCommand(
        () => _ = TestAsync(),
        () => !_testing && _cfg.Host.Trim().Length > 0 && _cfg.Username.Trim().Length > 0);
    private RelayCommand? _testCommand;

    private async Task TestAsync()
    {
        _testing = true;
        TestCommand.RaiseCanExecuteChanged();
        Status = $"Testing \\\\{_cfg.Host}\\{_cfg.Share}…";
        try
        {
            var (ok, message) = await Task.Run(() => RobotSmbUploader.Test(_cfg));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                Status = ok ? message : $"Failed — {message}");
        }
        finally
        {
            _testing = false;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                TestCommand.RaiseCanExecuteChanged());
        }
    }

    private void Refresh() => TestCommand.RaiseCanExecuteChanged();
}
