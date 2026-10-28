using System.Collections.ObjectModel;
using System.Numerics;
using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>Which drill gesture the pointer used.</summary>
internal enum DrillClickKind
{
    /// <summary>Ctrl-click: mark the edge under the cursor as a hole.</summary>
    MarkEdge,
    /// <summary>Alt-click: take that hole off the list. The circle stays.</summary>
    RemoveListed,
    /// <summary>Shift-click: place a hole on the face.</summary>
    AddFace,
    /// <summary>Shift-Alt-click: remove the hole placed on the face.</summary>
    RemoveFace,
}

/// <summary>One row in the HOLES list. Clicking it shows that hole's diameter and depth.</summary>
public sealed class DrillListItem : ViewModelBase
{
    string _name = "";

    public int Id { get; init; }
    public DrillSite Site { get; init; } = null!;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }
}

public sealed partial class ViewportViewModel
{
    readonly List<DrillSite> _drillSites = [];
    readonly List<IReadOnlyList<Vector3>> _drillEdges = [];
    Vector3[] _drillMesh = [];
    int _activeDrillId = -1;
    int _nextDrillId = 10_000;
    string _drillStatusText = "Find holes and edges. Ctrl-click an edge to drill it. Shift-click a face to place a hole.";
    RelayCommand<DrillListItem>? _selectDrillHoleCommand;
    RelayCommand? _removeDrillHoleCommand;

    /// <summary>Found holes and placed edge or face points. Click a row to edit that one.</summary>
    public ObservableCollection<DrillListItem> DrillHoles { get; } = [];

    public bool HasDrillHoles => DrillHoles.Count > 0;

    public string SelectedDrillHoleName
    {
        get
        {
            foreach (var item in DrillHoles)
                if (item.Id == _activeDrillId) return item.Name;
            return "";
        }
    }

    public ICommand SelectDrillHoleCommand => _selectDrillHoleCommand ??= new RelayCommand<DrillListItem>(SelectDrillHole);

    public RelayCommand RemoveDrillHoleCommand => _removeDrillHoleCommand ??=
        new RelayCommand(RemoveActiveDrill, () => ActiveDrill() is not null);

    public string DrillStatusText
    {
        get => _drillStatusText;
        private set => SetField(ref _drillStatusText, value);
    }

    public bool HasSelectedDrillPoint => ActiveDrill() is not null;

    public double SelectedDrillDiameterMm
    {
        get => ActiveDrill()?.DiameterMm ?? 0;
        set
        {
            var site = ActiveDrill();
            if (site is null) return;
            float next = MathF.Max(0.1f, (float)value);
            if (MathF.Abs(next - site.DetectedDiameterMm) < 0.05f)
                site.DiameterOverrideMm = null;
            else
                site.DiameterOverrideMm = next;
            OnPropertyChanged();
            RefreshDrillStatus();
            NotifyRenderNeeded();
        }
    }

    public double SelectedDrillDepthMm
    {
        get => ActiveDrill()?.DepthMm(BreakthroughMm()) ?? 0;
        set
        {
            var site = ActiveDrill();
            if (site is null) return;
            float next = MathF.Max(0.05f, (float)value);
            float auto = site.SpanMm + BreakthroughMm();
            if (MathF.Abs(next - auto) < 0.05f)
                site.DepthOverrideMm = null;
            else
                site.DepthOverrideMm = next;
            OnPropertyChanged();
            RefreshDrillStatus();
            NotifyRenderNeeded();
        }
    }

    public bool SelectedDrillIsFace => ActiveDrill()?.Kind == DrillSiteKind.Face;

    public double SelectedDrillX
    {
        get => ActiveDrill()?.Position.X ?? 0;
        set => MoveSelectedFace((float)value, null, null);
    }

    public double SelectedDrillY
    {
        get => ActiveDrill()?.Position.Y ?? 0;
        set => MoveSelectedFace(null, (float)value, null);
    }

    public double SelectedDrillZ
    {
        get => ActiveDrill()?.Position.Z ?? 0;
        set => MoveSelectedFace(null, null, (float)value);
    }

    public RelayCommand FindDrillFeaturesCommand => _findDrillFeaturesCommand ??= new RelayCommand(() =>
    {
        OnFindDrillFeaturesRequested?.Invoke();
    });
    RelayCommand? _findDrillFeaturesCommand;

    public RelayCommand AddFoundEdgesCommand => _addFoundEdgesCommand ??= new RelayCommand(AddFoundEdgesToCurves);
    RelayCommand? _addFoundEdgesCommand;

    void AddFoundEdgesToCurves()
    {
        var sub = SubtractiveSettings;
        if (sub is null) return;
        var found = FoundFeatureCurves();
        if (found.Count == 0)
        {
            sub.NoteCurveStatus("Find edges first, then add them here.", error: true);
            return;
        }
        int added = sub.AddCurves(found);
        sub.NoteCurveStatus(added == 0
            ? "Those edges are already in the curve list."
            : $"Added {added} curve(s) from the part.");
        NotifyRenderNeeded();
    }

    List<MillCurve> FoundFeatureCurves()
    {
        var list = new List<MillCurve>();
        int holes = 1;
        foreach (var site in _drillSites)
        {
            if (site.Kind != DrillSiteKind.Hole || site.Rim is not { Count: >= 2 }) continue;
            list.Add(new MillCurve
            {
                Name = $"Hole rim {holes++}",
                Closed = true,
                Included = true,
                Points = [.. site.Rim],
            });
        }
        int edges = 1;
        foreach (var edge in _drillEdges)
        {
            if (edge.Count < 2) continue;
            bool closed = Vector3.Distance(edge[0], edge[^1]) <= 0.05f;
            list.Add(new MillCurve
            {
                Name = $"Edge {edges++}",
                Closed = closed,
                Included = true,
                Points = [.. edge],
            });
        }
        return list;
    }

    internal Action? OnFindDrillFeaturesRequested { get; set; }

    internal void RememberDrillMesh(IReadOnlyList<Vector3> mesh)
    {
        if (mesh.Count == 0) return;
        _drillMesh = mesh as Vector3[] ?? mesh.ToArray();
    }

    internal void SetDrillSurvey(DrillSurvey survey, IReadOnlyList<Vector3> mesh)
    {
        _drillSites.Clear();
        _drillEdges.Clear();
        _drillEdges.AddRange(survey.Edges);
        foreach (var hole in survey.Holes)
            _drillSites.Add(hole);
        _drillMesh = mesh as Vector3[] ?? mesh.ToArray();
        _activeDrillId = -1;
        RefreshDrillStatus();
        NotifyDrillSelection();
        NotifyRenderNeeded();
    }

    internal void ApplyDrillClick(Vector3 hit, Vector3 faceNormal, Vector3 viewDir, DrillClickKind kind, float toolDiameterMm)
    {
        var axis = faceNormal.LengthSquared() > 1e-8f ? Vector3.Normalize(faceNormal) : Vector3.UnitZ;
        switch (kind)
        {
            case DrillClickKind.AddFace:
                AddFaceHole(hit, axis, toolDiameterMm);
                break;
            case DrillClickKind.RemoveFace:
                RemoveFaceHole(hit);
                break;
            case DrillClickKind.RemoveListed:
                RemoveListedHole(hit);
                break;
            default:
                MarkEdgeAsHole(hit, axis, viewDir, toolDiameterMm);
                break;
        }
        RefreshDrillStatus();
        NotifyDrillSelection();
        NotifyRenderNeeded();
    }

    void MarkEdgeAsHole(Vector3 hit, Vector3 axis, Vector3 viewDir, float toolDiameterMm)
    {
        var picked = NearestHole(hit);
        if (picked is not null)
        {
            picked.Included = true;
            _activeDrillId = picked.Id;
            return;
        }
        if (!TryEdge(hit, out var chain, out var center)) return;
        var existing = SiteForRim(chain);
        if (existing is not null)
        {
            existing.Included = true;
            _activeDrillId = existing.Id;
            return;
        }
        var loopAxis = axis;
        float diameter = toolDiameterMm;
        if (DrillFeatureFinder.TryLoopFrame(chain, out center, out var loopAxisFound, out float radius))
        {
            loopAxis = FaceLoop(loopAxisFound, axis, viewDir);
            if (radius > 0.5f) diameter = radius * 2f;
        }
        var site = NewPoint(DrillSiteKind.Hole, center, loopAxis, diameter);
        site.Rim = chain as List<Vector3> ?? chain.ToList();
        DrillFeatureFinder.TagOpening(site);
        _drillSites.Add(site);
        _activeDrillId = site.Id;
        _drillEdges.RemoveAll(c => ReferenceEquals(c, chain));
    }

    void RemoveListedHole(Vector3 hit)
    {
        if (TryEdge(hit, out var chain, out _))
        {
            var onRim = SiteForRim(chain);
            if (onRim is { Kind: DrillSiteKind.Edge })
            {
                DropSite(onRim);
                return;
            }
            if (onRim is { Kind: DrillSiteKind.Hole })
            {
                onRim.Included = false;
                _activeDrillId = onRim.Id;
                return;
            }
        }
        var edgePoint = NearestOfKind(hit, DrillSiteKind.Edge, 8f);
        if (edgePoint is not null)
        {
            DropSite(edgePoint);
            return;
        }
        var holeOff = NearestHole(hit);
        if (holeOff is null) return;
        holeOff.Included = false;
        _activeDrillId = holeOff.Id;
    }

    void AddFaceHole(Vector3 hit, Vector3 axis, float toolDiameterMm)
    {
        var site = NewPoint(DrillSiteKind.Face, hit, axis, toolDiameterMm);
        _drillSites.Add(site);
        _activeDrillId = site.Id;
    }

    void RemoveFaceHole(Vector3 hit)
    {
        var face = NearestOfKind(hit, DrillSiteKind.Face, 8f);
        if (face is not null) DropSite(face);
    }

    void DropSite(DrillSite site)
    {
        _drillSites.Remove(site);
        if (_activeDrillId == site.Id) _activeDrillId = -1;
    }

    static Vector3 FaceLoop(Vector3 loopAxis, Vector3 faceNormal, Vector3 viewDir)
    {
        if (loopAxis.LengthSquared() < 1e-8f) return faceNormal;
        loopAxis = Vector3.Normalize(loopAxis);
        if (Vector3.Dot(loopAxis, faceNormal) < -0.3f)
            loopAxis = -loopAxis;
        else if (viewDir.LengthSquared() > 1e-8f && Vector3.Dot(loopAxis, Vector3.Normalize(viewDir)) > 0.3f)
            loopAxis = -loopAxis;
        return loopAxis;
    }

    void MoveSelectedFace(float? x, float? y, float? z)
    {
        var site = ActiveDrill();
        if (site is null || site.Kind != DrillSiteKind.Face) return;
        var next = new Vector3(x ?? site.Position.X, y ?? site.Position.Y, z ?? site.Position.Z);
        if (Vector3.DistanceSquared(next, site.Position) < 1e-8f) return;
        site.Position = next;
        if (_drillMesh.Length > 0)
        {
            float probe = MathF.Max(8f, site.DetectedDiameterMm);
            site.SpanMm = DrillFeatureFinder.MeasureSpan(_drillMesh, site.Position, site.Axis, probe);
        }
        OnPropertyChanged(nameof(SelectedDrillX));
        OnPropertyChanged(nameof(SelectedDrillY));
        OnPropertyChanged(nameof(SelectedDrillZ));
        OnPropertyChanged(nameof(SelectedDrillDepthMm));
        RefreshDrillStatus();
        NotifyRenderNeeded();
    }

    internal IReadOnlyList<DrillPoint> CollectDrillPoints()
    {
        float breakthrough = BreakthroughMm();
        var list = new List<DrillPoint>();
        foreach (var site in _drillSites)
        {
            if (!site.Included) continue;
            var axis = site.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(site.Axis) : Vector3.UnitZ;
            list.Add(new DrillPoint(
                site.Position, axis, site.DepthMm(breakthrough), site.DiameterMm,
                site.Cut, site.MinorDiameterMm, site.ShoulderDepthMm, site.Rim));
        }
        return list;
    }

    internal IReadOnlyList<(Vector3 Pos, Vector3 Color)> DrillMarkerSegments() =>
        DrillMarkers.Build(_drillSites, _drillEdges, _activeDrillId, BreakthroughMm());

    internal bool SetActiveDrillDiameter(float mm)
    {
        if (ActiveDrill() is null) return false;
        SelectedDrillDiameterMm = mm;
        return true;
    }

    internal bool SetActiveDrillDepth(float mm)
    {
        if (ActiveDrill() is null) return false;
        SelectedDrillDepthMm = mm;
        return true;
    }

    void SelectDrillHole(DrillListItem? item)
    {
        if (item is null) return;
        _activeDrillId = item.Id;
        if (item.Site.Kind == DrillSiteKind.Hole)
            item.Site.Included = true;
        RefreshDrillStatus();
        NotifyDrillSelection();
        NotifyRenderNeeded();
    }

    void RemoveActiveDrill()
    {
        var site = ActiveDrill();
        if (site is null) return;
        if (site.Kind == DrillSiteKind.Hole)
            site.Included = false;
        else
            _drillSites.Remove(site);
        _activeDrillId = -1;
        RefreshDrillStatus();
        NotifyDrillSelection();
        NotifyRenderNeeded();
    }

    internal string DescribeDrill()
    {
        int holes = 0, edges = _drillEdges.Count, face = 0, edgePts = 0, included = 0;
        foreach (var s in _drillSites)
        {
            if (s.Kind == DrillSiteKind.Hole) holes++;
            else if (s.Kind == DrillSiteKind.Face) face++;
            else edgePts++;
            if (s.Included) included++;
        }
        var active = ActiveDrill();
        string edit = active is null
            ? "none selected"
            : $"selected {active.Kind} Ø{active.DiameterMm:0.#} mm × {active.DepthMm(BreakthroughMm()):0.#} mm";
        return $"{holes} holes, {edges} edges, {included} drill points ({face} on faces, {edgePts} on edges), {edit}";
    }

    DrillSite NewPoint(DrillSiteKind kind, Vector3 position, Vector3 axis, float toolDiameterMm)
    {
        float probe = MathF.Max(8f, toolDiameterMm);
        float span = _drillMesh.Length == 0
            ? 0f
            : DrillFeatureFinder.MeasureSpan(_drillMesh, position, axis, probe);
        return new DrillSite
        {
            Id = _nextDrillId++,
            Kind = kind,
            Position = position,
            Axis = axis,
            DetectedDiameterMm = MathF.Max(0.1f, toolDiameterMm),
            SpanMm = span,
            Included = true,
        };
    }

    DrillSite? NearestHole(Vector3 hit)
    {
        DrillSite? best = null;
        float bestRadial = float.MaxValue;
        foreach (var site in _drillSites)
        {
            if (site.Kind != DrillSiteKind.Hole) continue;
            var axis = site.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(site.Axis) : Vector3.UnitZ;
            var d = hit - site.Position;
            float axial = MathF.Abs(Vector3.Dot(d, axis));
            float radial = (d - axis * Vector3.Dot(d, axis)).Length();
            float limit = site.DetectedDiameterMm * 0.5f + 4f;
            float axialLimit = MathF.Max(8f, site.SpanMm + 2f);
            if (axial > axialLimit || radial > limit) continue;
            if (radial < bestRadial)
            {
                bestRadial = radial;
                best = site;
            }
        }
        return best;
    }

    bool TryEdge(Vector3 hit, out IReadOnlyList<Vector3> chain, out Vector3 center)
    {
        chain = [];
        center = hit;
        float best = 6f;
        bool found = false;
        foreach (var candidate in _drillEdges)
        {
            if (!DrillFeatureFinder.TryClosestOnChain(candidate, hit, out var p, out float d)) continue;
            if (d >= best) continue;
            best = d;
            chain = candidate;
            center = p;
            found = true;
        }
        if (found && DrillFeatureFinder.TryLoopFrame(chain, out var loopCenter, out _, out _))
            center = loopCenter;
        return found;
    }

    DrillSite? SiteForRim(IReadOnlyList<Vector3> chain)
    {
        foreach (var site in _drillSites)
        {
            if (site.Rim is not null && ReferenceEquals(site.Rim, chain))
                return site;
        }
        return null;
    }

    DrillSite? NearestOfKind(Vector3 hit, DrillSiteKind kind, float limit)
    {
        DrillSite? best = null;
        float bestD = limit;
        foreach (var site in _drillSites)
        {
            if (site.Kind != kind) continue;
            float d = Vector3.Distance(hit, site.Position);
            if (d <= bestD)
            {
                bestD = d;
                best = site;
            }
        }
        return best;
    }

    DrillSite? ActiveDrill()
    {
        foreach (var site in _drillSites)
            if (site.Id == _activeDrillId) return site;
        return null;
    }

    float BreakthroughMm() => (float)(SubtractiveSettings?.DrillingBreakthroughMm ?? 0);

    void RefreshDrillStatus()
    {
        DrillStatusText = DescribeDrill();
        SyncDrillList();
    }

    void SyncDrillList()
    {
        int holeN = 0, edgeN = 0, faceN = 0;
        var seen = new HashSet<int>();
        foreach (var site in _drillSites)
        {
            if (!site.Included) continue;
            seen.Add(site.Id);
            string kind;
            if (site.Kind == DrillSiteKind.Hole)
            {
                int n = ++holeN;
                kind = site.Cut switch
                {
                    DrillCutKind.Counterbore => $"Counterbore {n}",
                    DrillCutKind.Countersink => $"Countersink {n}",
                    DrillCutKind.Profile => $"{ProfileTitle(site.ShapeName)} {n}",
                    _ => $"Hole {n}",
                };
            }
            else if (site.Kind == DrillSiteKind.Edge)
                kind = $"Edge {++edgeN}";
            else
                kind = $"Face {++faceN}";
            bool stepped = site.Kind == DrillSiteKind.Hole
                && site.Cut is DrillCutKind.Counterbore or DrillCutKind.Countersink
                && site.MinorDiameterMm > 0.5f;
            string size = stepped
                ? $"Ø{site.DiameterMm:0.0} / Ø{site.MinorDiameterMm:0.0} mm"
                : site.Kind == DrillSiteKind.Hole && site.Cut == DrillCutKind.Profile
                    ? $"{site.DiameterMm:0.0} mm"
                    : $"Ø{site.DiameterMm:0.0} mm";
            string name = $"{kind}   {size}";
            DrillListItem? row = null;
            foreach (var item in DrillHoles)
            {
                if (item.Id != site.Id) continue;
                row = item;
                break;
            }
            if (row is null)
                DrillHoles.Add(new DrillListItem { Id = site.Id, Site = site, Name = name });
            else if (row.Name != name)
                row.Name = name;
        }
        for (int i = DrillHoles.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(DrillHoles[i].Id))
                DrillHoles.RemoveAt(i);
        }
        OnPropertyChanged(nameof(HasDrillHoles));
        OnPropertyChanged(nameof(SelectedDrillHoleName));
    }

    static string ProfileTitle(string? shape) =>
        string.IsNullOrWhiteSpace(shape) || shape == "Hole" ? "Polygon" : shape;

    void NotifyDrillSelection()
    {
        OnPropertyChanged(nameof(HasSelectedDrillPoint));
        OnPropertyChanged(nameof(SelectedDrillDiameterMm));
        OnPropertyChanged(nameof(SelectedDrillDepthMm));
        OnPropertyChanged(nameof(SelectedDrillHoleName));
        OnPropertyChanged(nameof(SelectedDrillIsFace));
        OnPropertyChanged(nameof(SelectedDrillX));
        OnPropertyChanged(nameof(SelectedDrillY));
        OnPropertyChanged(nameof(SelectedDrillZ));
        _removeDrillHoleCommand?.RaiseCanExecuteChanged();
    }
}
