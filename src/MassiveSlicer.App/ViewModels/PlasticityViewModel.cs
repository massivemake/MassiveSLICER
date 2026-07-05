using Avalonia.Threading;
using MassiveSlicer.App;
using MassiveSlicer.App.Plasticity;
using MassiveSlicer.Commands;
using MassiveSlicer.ViewModels.Base;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Drives the Plasticity live bridge (right-panel "PLASTICITY" tab). Connects to a running
/// Plasticity server, mirrors its solid/sheet objects into the scene as importable meshes, and
/// keeps them in sync as the model is edited — optionally re-slicing on every update. Mirrors the
/// Plasticity Blender bridge: Connect → live link → geometry updates in place.
/// Call <see cref="Attach"/> once after construction to wire it to the viewport.
/// </summary>
public sealed class PlasticityViewModel : ViewModelBase
{
    private readonly PlasticityClient _client = new();
    private readonly Dictionary<int, SceneNode> _nodes = new();

    private ViewportViewModel? _viewport;
    private Action<string>? _log;
    private SceneNode? _lastUpdated;

    private string _serverAddress = "localhost:8980";
    private bool   _isConnected;
    private bool   _isBusy;
    private bool   _liveLink   = true;
    private bool   _autoSlice;
    private double _unitScaleMm = 1000.0;   // Plasticity works in metres; the slicer in millimetres.
    private string _status = "Not connected.";
    private int    _objectCount;

    public PlasticityViewModel()
    {
        _client.Connected        += ()      => Post(OnClientConnected);
        _client.Disconnected     += reason  => Post(() => OnClientDisconnected(reason));
        _client.ObjectsReceived  += objs    => Post(() => ApplyObjects(objs));
        _client.ObjectsDeleted   += ids     => Post(() => ApplyDeletes(ids));
        _client.Log              += msg     => Post(() => _log?.Invoke($"[plasticity] {msg}"));

        ConnectCommand    = new RelayCommand(() => _ = ConnectAsync(),    () => !IsConnected && !IsBusy);
        DisconnectCommand = new RelayCommand(() => _ = DisconnectAsync(), () => IsConnected);
        RefreshCommand    = new RelayCommand(() => _ = _client.RefreshAsync(),    () => IsConnected);
        SliceNowCommand   = new RelayCommand(SliceActive,                         () => IsConnected && _lastUpdated is not null);
    }

    /// <summary>Wires the bridge to the viewport (scene add/update/remove + slicing) and the console.</summary>
    public void Attach(ViewportViewModel viewport, Action<string> log)
    {
        _viewport = viewport;
        _log = log;
    }

    // -- Bindable state ----------------------------------------------------

    /// <summary>Plasticity server address, "host:port" (default localhost:8980).</summary>
    public string ServerAddress
    {
        get => _serverAddress;
        set => SetField(ref _serverAddress, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetField(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(IsDisconnected));
            RaiseCommands();
        }
    }

    /// <summary>Convenience inverse for XAML visibility bindings.</summary>
    public bool IsDisconnected => !_isConnected;

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetField(ref _isBusy, value)) RaiseCommands(); }
    }

    /// <summary>When on, the server pushes a transaction on every edit and the scene updates live.</summary>
    public bool LiveLink
    {
        get => _liveLink;
        set
        {
            if (!SetField(ref _liveLink, value)) return;
            if (IsConnected)
                _ = value ? _client.SubscribeAsync() : _client.UnsubscribeAsync();
        }
    }

    /// <summary>When on, each geometry update re-slices the affected object automatically.</summary>
    public bool AutoSlice
    {
        get => _autoSlice;
        set => SetField(ref _autoSlice, value);
    }

    /// <summary>Metres→millimetres conversion applied to incoming geometry (default 1000).</summary>
    public double UnitScaleMm
    {
        get => _unitScaleMm;
        set => SetField(ref _unitScaleMm, value <= 0 ? 1.0 : value);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>Number of Plasticity objects currently mirrored into the scene.</summary>
    public int ObjectCount
    {
        get => _objectCount;
        private set => SetField(ref _objectCount, value);
    }

    public RelayCommand ConnectCommand    { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand RefreshCommand    { get; }
    public RelayCommand SliceNowCommand   { get; }

    // -- Client event handlers (already marshalled to the UI thread) -------

    private async Task ConnectAsync()
    {
        IsBusy = true;
        Status = $"Connecting to {ServerAddress}…";
        try
        {
            await _client.ConnectAsync(ServerAddress, LiveLink, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Status = $"Connect failed — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectAsync()
    {
        // Manual disconnect cancels the receive loop silently (no server "closed" event),
        // so update the bound state here rather than relying on the Disconnected event.
        await _client.DisconnectAsync();
        IsConnected = false;
        Status = "Disconnected.";
        _log?.Invoke("[plasticity] disconnected.");
    }

    private void OnClientConnected()
    {
        IsConnected = true;
        Status = $"Connected to {ServerAddress}" + (LiveLink ? " (live)." : ".");
        _log?.Invoke($"[plasticity] connected to {ServerAddress}.");
    }

    private void OnClientDisconnected(string reason)
    {
        IsConnected = false;
        Status = $"Disconnected — {reason}";
        _log?.Invoke($"[plasticity] disconnected — {reason}");
    }

    private void ApplyObjects(IReadOnlyList<PlasticityObject> objs)
    {
        if (_viewport is null) return;

        int applied = 0;
        foreach (var o in objs)
        {
            if (o.Positions.Length < 9 || o.Indices.Length < 3) continue;   // need at least one triangle
            var mesh = BuildMesh(o);

            if (_nodes.TryGetValue(o.Id, out var node))
            {
                node.PendingMesh = mesh;
                _viewport.PendingModelRefresh.Enqueue(node);
            }
            else
            {
                node = new SceneNode { Name = mesh.Name, PendingMesh = mesh };
                ImportHelper.PlaceOnBed(node, _viewport.ActiveCell);
                _viewport.AddImportNode(node);
                _nodes[o.Id] = node;
            }

            _lastUpdated = node;
            applied++;
        }

        _viewport.NotifyRenderNeeded();
        ObjectCount = _nodes.Count;
        RaiseCommands();

        if (applied > 0)
        {
            _log?.Invoke($"[plasticity] synced {applied} object(s); {_nodes.Count} linked.");
            if (AutoSlice) SliceActive();
        }
    }

    private void ApplyDeletes(IReadOnlyList<int> ids)
    {
        if (_viewport is null) return;

        foreach (var id in ids)
        {
            if (!_nodes.TryGetValue(id, out var node)) continue;
            _viewport.RequestDeleteNode(node);
            _nodes.Remove(id);
            if (ReferenceEquals(_lastUpdated, node)) _lastUpdated = null;
        }

        _viewport.NotifyRenderNeeded();
        ObjectCount = _nodes.Count;
        RaiseCommands();
    }

    private void SliceActive()
    {
        if (_viewport is null || _lastUpdated is null) return;

        _viewport.ForceSelectNode?.Invoke(_lastUpdated);
        if (_viewport.SliceCommand.CanExecute(null))
        {
            _viewport.SliceCommand.Execute(null);
            _log?.Invoke("[plasticity] auto-slice triggered.");
        }
        else
        {
            _log?.Invoke("[plasticity] slice skipped (already slicing or nothing to slice).");
        }
    }

    // -- Mesh conversion ---------------------------------------------------

    private MeshData BuildMesh(PlasticityObject o)
    {
        float s = (float)UnitScaleMm;
        int vcount = o.Positions.Length / 3;

        var positions = new Vector3[vcount];
        for (int i = 0; i < vcount; i++)
            positions[i] = new Vector3(o.Positions[i * 3] * s,
                                       o.Positions[i * 3 + 1] * s,
                                       o.Positions[i * 3 + 2] * s);

        var indices = new uint[o.Indices.Length];
        for (int i = 0; i < o.Indices.Length; i++)
            indices[i] = (uint)o.Indices[i];

        Vector3[] normals;
        if (o.Normals.Length == o.Positions.Length && vcount > 0)
        {
            normals = new Vector3[vcount];
            for (int i = 0; i < vcount; i++)
                normals[i] = new Vector3(o.Normals[i * 3], o.Normals[i * 3 + 1], o.Normals[i * 3 + 2]);
        }
        else
        {
            normals = ComputeNormals(positions, indices);
        }

        return new MeshData(positions, normals, indices, o.Name);
    }

    /// <summary>Area-weighted vertex normals from an indexed triangle list.</summary>
    private static Vector3[] ComputeNormals(Vector3[] positions, uint[] indices)
    {
        var normals = new Vector3[positions.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
            if (a >= positions.Length || b >= positions.Length || c >= positions.Length) continue;
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += n; normals[b] += n; normals[c] += n;
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitZ;
        return normals;
    }

    private void RaiseCommands()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        SliceNowCommand.RaiseCanExecuteChanged();
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
