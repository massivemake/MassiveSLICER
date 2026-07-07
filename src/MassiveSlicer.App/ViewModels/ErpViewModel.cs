using System.Collections.ObjectModel;
using Avalonia.Threading;
using MassiveSlicer.App.Erp;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

public enum ErpConnectionState { Disconnected, Connecting, Connected }

/// <summary>
/// ERP "Project Attachment" dock (bottom-left of the viewport, phase 1):
/// connection settings (base URL + bearer token, persisted in prefs),
/// combined Project/Lead quick-search, element picker, and attaching the
/// current workspace to the chosen record. The attachment is persisted in
/// the .mass file and displays offline (independent of connection state).
/// </summary>
public sealed class ErpViewModel : ViewModelBase
{
    private AppPreferences? _prefs;
    private Action? _savePrefs;
    private Action<string>? _log;
    private ErpClient? _client;

    private CancellationTokenSource? _searchCts;
    private bool _busy;

    /// <summary>Wires prefs persistence + console logging. Call once at startup.</summary>
    public void Initialize(AppPreferences prefs, Action savePrefs, Action<string> log)
    {
        _prefs     = prefs;
        _savePrefs = savePrefs;
        _log       = log;
        _baseUrl   = prefs.ErpBaseUrl;
        _apiToken  = prefs.ErpApiToken ?? "";
        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(ApiToken));
    }

    // -- Dock chrome ---------------------------------------------------------

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
                OnPropertyChanged(nameof(ToggleIcon));
        }
    }

    public RelayCommand ToggleExpandedCommand => _toggleExpandedCommand ??=
        new RelayCommand(() => IsExpanded = !IsExpanded);
    private RelayCommand? _toggleExpandedCommand;

    public string ToggleIcon => _isExpanded ? "mdi-chevron-down" : "mdi-briefcase-outline";

    /// <summary>Dock button badge: "ERP" unattached, else "25-114 · Element 3".</summary>
    public string ToggleLabel => _attachment is null
        ? "ERP"
        : _attachment.ElementName is { Length: > 0 } el
            ? $"{_attachment.Number} · {el}"
            : _attachment.Number;

    // -- Connection settings ---------------------------------------------------

    private string _baseUrl = "";
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (!SetField(ref _baseUrl, value)) return;
            if (_prefs is not null) { _prefs.ErpBaseUrl = value.Trim(); _savePrefs?.Invoke(); }
            InvalidateClient();
        }
    }

    private string _apiToken = "";
    public string ApiToken
    {
        get => _apiToken;
        set
        {
            if (!SetField(ref _apiToken, value)) return;
            if (_prefs is not null) { _prefs.ErpApiToken = value.Trim().Length > 0 ? value.Trim() : null; _savePrefs?.Invoke(); }
            InvalidateClient();
        }
    }

    private void InvalidateClient()
    {
        _client?.Dispose();
        _client = null;
        if (ConnectionState == ErpConnectionState.Connected)
        {
            ConnectionState = ErpConnectionState.Disconnected;
            Status = "Settings changed — reconnect.";
        }
    }

    private ErpConnectionState _connectionState = ErpConnectionState.Disconnected;
    public ErpConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (!SetField(ref _connectionState, value)) return;
            OnPropertyChanged(nameof(IsConnected));
            NotifySectionVisibility();
            ConnectCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsConnected => _connectionState == ErpConnectionState.Connected;

    private string _status = "Not connected.";
    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public RelayCommand ConnectCommand => _connectCommand ??= new RelayCommand(
        () => _ = ConnectAsync(),
        () => !_busy && _baseUrl.Trim().Length > 0 && _apiToken.Trim().Length > 0);
    private RelayCommand? _connectCommand;

    private async Task ConnectAsync()
    {
        if (_busy) return;
        _busy = true;
        ConnectCommand.RaiseCanExecuteChanged();
        ConnectionState = ErpConnectionState.Connecting;
        Status = "Connecting…";
        try
        {
            _client?.Dispose();
            _client = new ErpClient(_baseUrl, _apiToken);
            var ping = await _client.PingAsync(CancellationToken.None);
            if (ping.Ok)
            {
                ConnectionState = ErpConnectionState.Connected;
                Status = $"Connected to {new Uri(_baseUrl.Trim().TrimEnd('/') + "/").Host}";
                _log?.Invoke($"[erp] connected to {_baseUrl.Trim()}");
                _showSettingsRequested = false;
                NotifySectionVisibility();
            }
            else
            {
                ConnectionState = ErpConnectionState.Disconnected;
                Status = ping.Error!.Kind switch
                {
                    ErpErrorKind.Unauthorized => "Token invalid or revoked.",
                    ErpErrorKind.Timeout      => "ERP unreachable — timed out.",
                    _                         => $"Connect failed — {ping.Error.Message}",
                };
                _log?.Invoke($"[erp] connect failed: {ping.Error.Kind} — {ping.Error.Message}");
            }
        }
        catch (Exception ex)
        {
            ConnectionState = ErpConnectionState.Disconnected;
            Status = $"Connect failed — {ex.Message}";
        }
        finally
        {
            _busy = false;
            Post(() => ConnectCommand.RaiseCanExecuteChanged());
        }
    }

    // -- Search ---------------------------------------------------------------

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value)) return;
            ScheduleSearch(value);
        }
    }

    public ObservableCollection<ErpSearchHit> SearchResults { get; } = [];

    private ErpSearchHit? _selectedResult;
    public ErpSearchHit? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (!SetField(ref _selectedResult, value)) return;
            OnPropertyChanged(nameof(HasElements));
            AttachCommand.RaiseCanExecuteChanged();
            _ = PopulateElementsAsync(value);
        }
    }

    public ObservableCollection<ErpElement> Elements { get; } = [];
    public bool HasElements => Elements.Count > 0;

    private ErpElement? _selectedElement;
    public ErpElement? SelectedElement
    {
        get => _selectedElement;
        set => SetField(ref _selectedElement, value);
    }

    /// <summary>Debounced (300 ms) search; cancels the previous debounce AND its in-flight request.</summary>
    private void ScheduleSearch(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        if (query.Trim().Length < 2)
        {
            SearchResults.Clear();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, cts.Token);
                var client = _client;
                if (client is null) return;
                var result = await client.SearchAsync(query.Trim(), cts.Token);
                if (cts.Token.IsCancellationRequested) return;
                Post(() =>
                {
                    // Drop stale responses that lost the race with a newer keystroke.
                    if (!ReferenceEquals(cts, _searchCts) || _searchText != query) return;
                    SearchResults.Clear();
                    if (result.Ok)
                    {
                        foreach (var hit in result.Value!)
                            SearchResults.Add(hit);
                        Status = result.Value!.Count == 0 ? "No matches." : $"{result.Value!.Count} match(es).";
                    }
                    else
                    {
                        Status = result.Error!.Kind == ErpErrorKind.Unauthorized
                            ? "Token invalid or revoked."
                            : $"Search failed — {result.Error.Message}";
                    }
                });
            }
            catch (OperationCanceledException) { /* superseded keystroke */ }
        });
    }

    private async Task PopulateElementsAsync(ErpSearchHit? hit)
    {
        Post(() => { Elements.Clear(); SelectedElement = null; OnPropertyChanged(nameof(HasElements)); });
        if (hit is null) return;

        if (hit.Elements.Count > 0)
        {
            Post(() =>
            {
                foreach (var e in hit.Elements) Elements.Add(e);
                OnPropertyChanged(nameof(HasElements));
            });
            return;
        }

        if (hit.Type != "project" || _client is null) return;
        var result = await _client.GetProjectElementsAsync(hit.Id, CancellationToken.None);
        if (!result.Ok) return;
        Post(() =>
        {
            if (!ReferenceEquals(hit, _selectedResult)) return;
            foreach (var e in result.Value!) Elements.Add(e);
            OnPropertyChanged(nameof(HasElements));
        });
    }

    // -- Element creation ---------------------------------------------------------

    /// <summary>Default name for a new element (workspace/model name); wired by MainWindowViewModel.</summary>
    public Func<string?>? GetDefaultElementName { get; set; }

    private string _newElementName = "";
    public string NewElementName
    {
        get => _newElementName;
        set { if (SetField(ref _newElementName, value)) CreateElementCommand.RaiseCanExecuteChanged(); }
    }

    public RelayCommand CreateElementCommand => _createElementCommand ??= new RelayCommand(
        () => _ = CreateElementForSearchAsync(),
        () => !_busy && IsConnected && _selectedResult is not null && _newElementName.Trim().Length > 0);
    private RelayCommand? _createElementCommand;

    private async Task CreateElementForSearchAsync()
    {
        var hit = _selectedResult;
        var client = _client;
        if (hit is null || client is null) return;

        string name = _newElementName.Trim();
        Status = $"Creating element on {hit.Number}…";
        var result = await client.CreateElementAsync(hit.Type, hit.Id, name, null, CancellationToken.None);
        Post(() =>
        {
            if (!result.Ok)
            {
                Status = $"Element create failed — {result.Error!.Message}";
                _log?.Invoke($"[erp] element create failed: {result.Error.Kind} — {result.Error.Message}");
                return;
            }
            var el = result.Value!;
            if (ReferenceEquals(hit, _selectedResult))
            {
                Elements.Add(el);
                OnPropertyChanged(nameof(HasElements));
                SelectedElement = el;
            }
            NewElementName = "";
            Status = $"Element \"{el.Name}\" created.";
            _log?.Invoke($"[erp] created element {el.Name} on {hit.Type} {hit.Number}");
        });
    }

    // -- Attachment -------------------------------------------------------------

    private ErpAttachment? _attachment;
    public ErpAttachment? Attachment => _attachment;

    public string AttachmentSummary => _attachment is null
        ? ""
        : $"{(_attachment.Type == "lead" ? "Lead" : "Project")} {_attachment.Number} — {_attachment.Title}"
          + (_attachment.ElementName is { Length: > 0 } el ? $"\nElement: {el}" : "");

    public RelayCommand AttachCommand => _attachCommand ??= new RelayCommand(Attach,
        () => _selectedResult is not null);
    private RelayCommand? _attachCommand;

    private void Attach()
    {
        var hit = _selectedResult;
        if (hit is null) return;
        SetAttachment(new ErpAttachment
        {
            Type        = hit.Type,
            Id          = hit.Id,
            Number      = hit.Number,
            Title       = hit.Title,
            ElementId   = _selectedElement?.Id,
            ElementName = _selectedElement is null
                ? null
                : _selectedElement.ElementNumber is { Length: > 0 } n
                    ? $"Element {n}"
                    : _selectedElement.Name,
        });
        _log?.Invoke($"[erp] attached workspace to {hit.Type} {hit.Number} ({hit.Title})"
                     + (_selectedElement is not null ? $", element {_selectedElement.Name}" : ""));
    }

    public RelayCommand DetachCommand => _detachCommand ??= new RelayCommand(() =>
    {
        _log?.Invoke($"[erp] detached from {_attachment?.Number}");
        SetAttachment(null);
    });
    private RelayCommand? _detachCommand;

    /// <summary>Back to the search view keeping the current results (pick a different record).</summary>
    public RelayCommand ChangeCommand => _changeCommand ??= new RelayCommand(() => SetAttachment(null));
    private RelayCommand? _changeCommand;

    private void SetAttachment(ErpAttachment? attachment)
    {
        _attachment = attachment;
        if (attachment is not null && string.IsNullOrEmpty(attachment.ElementId)
            && _attachmentElementName.Trim().Length == 0)
        {
            AttachmentElementName = GetDefaultElementName?.Invoke() ?? "";
        }
        OnPropertyChanged(nameof(Attachment));
        OnPropertyChanged(nameof(AttachmentSummary));
        OnPropertyChanged(nameof(ToggleLabel));
        NotifySectionVisibility();
    }

    /// <summary>Workspace open: restore the persisted attachment (works offline).
    /// When the workspace carries an attachment and the local prefs have ERP
    /// credentials, connect automatically so the project link is live on open.</summary>
    public void RestoreAttachment(ErpAttachment? attachment) => Post(() =>
    {
        SetAttachment(attachment);
        if (attachment is not null
            && ConnectionState == ErpConnectionState.Disconnected
            && _baseUrl.Trim().Length > 0
            && _apiToken.Trim().Length > 0)
        {
            _ = ConnectAsync();
        }
    });

    /// <summary>New workspace: no attachment.</summary>
    public void ClearAttachment() => Post(() => SetAttachment(null));

    /// <summary>Copy captured into the .mass document (no aliasing with live state).</summary>
    public ErpAttachment? CurrentAttachment => _attachment is null ? null : new ErpAttachment
    {
        Type        = _attachment.Type,
        Id          = _attachment.Id,
        Number      = _attachment.Number,
        Title       = _attachment.Title,
        ElementId   = _attachment.ElementId,
        ElementName = _attachment.ElementName,
    };

    // -- Attachment element creation ----------------------------------------------

    /// <summary>The attached project/lead has no element yet — offer to create one.</summary>
    public bool ShowAttachmentElementCreate =>
        _attachment is not null && string.IsNullOrEmpty(_attachment.ElementId) && IsConnected;

    private string _attachmentElementName = "";
    public string AttachmentElementName
    {
        get => _attachmentElementName;
        set { if (SetField(ref _attachmentElementName, value)) CreateAttachmentElementCommand.RaiseCanExecuteChanged(); }
    }

    public RelayCommand CreateAttachmentElementCommand => _createAttachmentElementCommand ??= new RelayCommand(
        () => _ = CreateElementForAttachmentAsync(),
        () => !_busy && ShowAttachmentElementCreate && _attachmentElementName.Trim().Length > 0);
    private RelayCommand? _createAttachmentElementCommand;

    private async Task CreateElementForAttachmentAsync()
    {
        var att = _attachment;
        var client = _client;
        if (att is null || client is null) return;

        string name = _attachmentElementName.Trim();
        Status = $"Creating element on {att.Number}…";
        var result = await client.CreateElementAsync(att.Type, att.Id, name, null, CancellationToken.None);
        Post(() =>
        {
            if (!result.Ok)
            {
                Status = $"Element create failed — {result.Error!.Message}";
                _log?.Invoke($"[erp] element create failed: {result.Error.Kind} — {result.Error.Message}");
                return;
            }
            var el = result.Value!;
            if (ReferenceEquals(att, _attachment))
            {
                att.ElementId = el.Id;
                att.ElementName = el.ElementNumber is { Length: > 0 } n ? $"Element {n}" : el.Name;
                SetAttachment(att);
            }
            Status = $"Element \"{el.Name}\" created and linked — save the workspace to keep it.";
            _log?.Invoke($"[erp] created element {el.Name} on {att.Type} {att.Number} and linked the workspace");
        });
    }

    // -- Slice registration --------------------------------------------------------

    /// <summary>Builds the slice payload (renders the preview PNG beside the .mass and
    /// resolves UNAS share-relative paths). Wired by MainWindowViewModel; returns null
    /// when the workspace has never been saved.</summary>
    public Func<Task<(ErpSliceStats Stats, IReadOnlyList<ErpSliceFile> Files)?>>? BuildSlicePayloadAsync { get; set; }

    public bool CanSendSlice => IsConnected && _attachment?.ElementId is { Length: > 0 } && !_busy;

    public RelayCommand SendSliceCommand => _sendSliceCommand ??= new RelayCommand(
        () => _ = SendSliceAsync(),
        () => CanSendSlice && BuildSlicePayloadAsync is not null);
    private RelayCommand? _sendSliceCommand;

    private async Task SendSliceAsync()
    {
        var att = _attachment;
        var client = _client;
        var build = BuildSlicePayloadAsync;
        if (att?.ElementId is not { Length: > 0 } elementId || client is null || build is null) return;

        _busy = true;
        SendSliceCommand.RaiseCanExecuteChanged();
        try
        {
            Status = "Preparing slice package…";
            var payload = await build();
            if (payload is null)
            {
                Post(() => Status = "Nothing to send — save the workspace first.");
                return;
            }
            Status = "Registering slice with ERP…";
            var result = await client.RegisterSliceAsync(
                elementId, payload.Value.Stats, payload.Value.Files, CancellationToken.None);
            Post(() =>
            {
                if (result.Ok)
                {
                    Status = $"Slice registered as Rev {result.Value!.Rev}.";
                    _log?.Invoke($"[erp] slice registered as rev {result.Value.Rev} on element {att.ElementName ?? elementId} ({att.Number})");
                }
                else
                {
                    Status = $"Slice register failed — {result.Error!.Message}";
                    _log?.Invoke($"[erp] slice register failed: {result.Error.Kind} — {result.Error.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Post(() => Status = $"Slice send failed — {ex.Message}");
        }
        finally
        {
            _busy = false;
            Post(() =>
            {
                SendSliceCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanSendSlice));
            });
        }
    }

    // -- Section visibility ------------------------------------------------------

    private bool _showSettingsRequested;
    public RelayCommand OpenSettingsCommand => _openSettingsCommand ??= new RelayCommand(() =>
    {
        _showSettingsRequested = true;
        NotifySectionVisibility();
    });
    private RelayCommand? _openSettingsCommand;

    public bool ShowSettings   => _attachment is null && (_showSettingsRequested || !IsConnected);
    public bool ShowSearch     => _attachment is null && IsConnected && !_showSettingsRequested;
    public bool ShowAttachment => _attachment is not null;

    private void NotifySectionVisibility()
    {
        OnPropertyChanged(nameof(ShowSettings));
        OnPropertyChanged(nameof(ShowSearch));
        OnPropertyChanged(nameof(ShowAttachment));
        OnPropertyChanged(nameof(ShowAttachmentElementCreate));
        OnPropertyChanged(nameof(CanSendSlice));
        CreateElementCommand.RaiseCanExecuteChanged();
        CreateAttachmentElementCommand.RaiseCanExecuteChanged();
        SendSliceCommand.RaiseCanExecuteChanged();
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
