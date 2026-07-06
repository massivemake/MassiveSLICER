using Avalonia.Threading;
using MassiveSlicer.App;
using MassiveSlicer.App.MassiveBrain;
using MassiveSlicer.App.Plasticity;
using MassiveSlicer.ViewModels.Base;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// MassiveBRAIN: MassiveSLICER-hosted live sync server (default localhost:4547).
/// Blender and Rhino addons connect as WebSocket clients and push geometry using
/// the Plasticity bridge wire format; incoming Add/Update/Delete transactions are
/// mirrored into the scene exactly like the Plasticity live bridge does.
/// </summary>
public sealed class MassiveBrainViewModel : ViewModelBase
{
    private readonly MassiveBrainServer _server = new();
    private readonly Dictionary<(int Client, int Id), SceneNode> _nodes = new();

    private ViewportViewModel? _viewport;
    private Action<string>? _log;
    private SceneNode? _lastUpdated;

    private bool   _enabled;
    private string _host = "localhost";
    private string _port = "4547";
    private bool   _autoSlice;
    private double _unitScaleMm = 1000.0;   // wire format is metres (Plasticity convention).
    private string _status = "Server off.";
    private int    _clientCount;
    private int    _objectCount;

    public MassiveBrainViewModel()
    {
        _server.ClientConnected    += (id, who)    => Post(() => OnClientConnected(id, who));
        _server.ClientDisconnected += (id, why)    => Post(() => OnClientDisconnected(id, why));
        _server.ObjectsReceived    += (id, objs)   => Post(() => ApplyObjects(id, objs));
        _server.ObjectsDeleted     += (id, ids)    => Post(() => ApplyDeletes(id, ids));
        _server.Log                += msg          => Post(() => _log?.Invoke($"[massivebrain] {msg}"));
    }

    /// <summary>Wires the server to the viewport (scene add/update/remove + slicing) and the console.</summary>
    public void Attach(ViewportViewModel viewport, Action<string> log)
    {
        _viewport = viewport;
        _log = log;
    }

    // -- Bindable state ----------------------------------------------------

    /// <summary>Starts/stops the WebSocket server.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetField(ref _enabled, value)) return;
            if (value) StartServer();
            else       StopServer();
        }
    }

    /// <summary>Bind host. "localhost" = local only; "0.0.0.0" accepts LAN clients.</summary>
    public string Host
    {
        get => _host;
        set => SetField(ref _host, value);
    }

    public string Port
    {
        get => _port;
        set => SetField(ref _port, value);
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

    /// <summary>Connected DCC clients (Blender/Rhino sessions).</summary>
    public int ClientCount
    {
        get => _clientCount;
        private set => SetField(ref _clientCount, value);
    }

    /// <summary>Number of synced objects currently mirrored into the scene.</summary>
    public int ObjectCount
    {
        get => _objectCount;
        private set => SetField(ref _objectCount, value);
    }

    // -- Server lifecycle ----------------------------------------------------

    private void StartServer()
    {
        if (!int.TryParse(Port.Trim(), out int port) || port is <= 0 or > 65535)
        {
            Status = $"Invalid port '{Port}'.";
            _enabled = false;
            OnPropertyChanged(nameof(Enabled));
            return;
        }

        try
        {
            _server.Start(Host, port);
            Status = $"Listening on {Host}:{port} — waiting for Blender/Rhino.";
            _log?.Invoke($"[massivebrain] server enabled on {Host}:{port}.");
        }
        catch (Exception ex)
        {
            Status = $"Start failed — {ex.Message}";
            _log?.Invoke($"[massivebrain] start failed: {ex.Message}");
            _enabled = false;
            OnPropertyChanged(nameof(Enabled));
        }
    }

    private void StopServer()
    {
        _server.Stop();
        ClientCount = 0;
        Status = "Server off.";
        _log?.Invoke("[massivebrain] server disabled.");
    }

    private void OnClientConnected(int id, string who)
    {
        ClientCount = _server.ClientCount;
        Status = $"{ClientCount} client(s) connected.";
        _log?.Invoke($"[massivebrain] client #{id} connected ({who}).");
    }

    private void OnClientDisconnected(int id, string why)
    {
        ClientCount = _server.ClientCount;
        Status = _enabled
            ? (ClientCount > 0 ? $"{ClientCount} client(s) connected." : "Listening — no clients.")
            : "Server off.";
        _log?.Invoke($"[massivebrain] client #{id} disconnected ({why}). Synced objects stay in the scene.");
    }

    // -- Scene integration (mirrors PlasticityViewModel) ---------------------

    private void ApplyObjects(int clientId, IReadOnlyList<PlasticityObject> objs)
    {
        if (_viewport is null) return;

        int applied = 0;
        foreach (var o in objs)
        {
            if (o.Positions.Length < 9 || o.Indices.Length < 3) continue;   // need at least one triangle
            var mesh = BuildMesh(o);

            var key = (clientId, o.Id);
            if (_nodes.TryGetValue(key, out var node))
            {
                node.PendingMesh = mesh;
                _viewport.PendingModelRefresh.Enqueue(node);
            }
            else
            {
                node = new SceneNode { Name = mesh.Name, PendingMesh = mesh };
                ImportHelper.PlaceOnBed(node, _viewport.ActiveCell);
                _viewport.AddImportNode(node);
                _nodes[key] = node;
            }

            _lastUpdated = node;
            applied++;
        }

        _viewport.NotifyRenderNeeded();
        ObjectCount = _nodes.Count;

        if (applied > 0)
        {
            _log?.Invoke($"[massivebrain] synced {applied} object(s) from client #{clientId}; {_nodes.Count} linked.");
            if (AutoSlice) SliceActive();
        }
    }

    private void ApplyDeletes(int clientId, IReadOnlyList<int> ids)
    {
        if (_viewport is null) return;

        foreach (var id in ids)
        {
            var key = (clientId, id);
            if (!_nodes.TryGetValue(key, out var node)) continue;
            _viewport.RequestDeleteNode(node);
            _nodes.Remove(key);
            if (ReferenceEquals(_lastUpdated, node)) _lastUpdated = null;
        }

        _viewport.NotifyRenderNeeded();
        ObjectCount = _nodes.Count;
    }

    private void SliceActive()
    {
        if (_viewport is null || _lastUpdated is null) return;

        _viewport.ForceSelectNode?.Invoke(_lastUpdated);
        if (_viewport.SliceCommand.CanExecute(null))
        {
            _viewport.SliceCommand.Execute(null);
            _log?.Invoke("[massivebrain] auto-slice triggered.");
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

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
