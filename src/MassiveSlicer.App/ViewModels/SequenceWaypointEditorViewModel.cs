using System.Globalization;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>Inspect / edit a tool-change sequence waypoint (Global_Points.dat).</summary>
public sealed class SequenceWaypointEditorViewModel : ViewModelBase
{
    static KrlGlobalPointsEditor.PointPose? _copiedPose;

    readonly ViewportViewModel _owner;
    ToolChangeWaypoint? _waypoint;
    KrlGlobalPointsEditor.PointPose _original = default;
    string? _sequenceId;

    public SequenceWaypointEditorViewModel(ViewportViewModel owner) => _owner = owner;

    public RelayCommand CloseCommand { get; private set; } = null!;
    public RelayCommand ResetCommand { get; private set; } = null!;
    public RelayCommand SaveCommand { get; private set; } = null!;
    public RelayCommand CopyAllCommand { get; private set; } = null!;
    public RelayCommand PasteAllCommand { get; private set; } = null!;

    public void WireCommands()
    {
        CloseCommand  = new RelayCommand(Close);
        ResetCommand  = new RelayCommand(Reset, () => IsEditable);
        SaveCommand   = new RelayCommand(Save, () => IsEditable && IsOpen);
        CopyAllCommand = new RelayCommand(CopyAll, () => Kind != "joint");
        PasteAllCommand = new RelayCommand(PasteAll, () => IsEditable && _copiedPose is not null);
    }

    bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (!SetField(ref _isOpen, value)) return;
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    public bool IsVisible => IsOpen;

    int _waypointIndex = -1;
    public int WaypointIndex => _waypointIndex;

    string _title = "";
    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    string _subtitle = "";
    public string Subtitle
    {
        get => _subtitle;
        private set => SetField(ref _subtitle, value);
    }

    string _note = "";
    public string Note
    {
        get => _note;
        private set
        {
            if (!SetField(ref _note, value)) return;
            OnPropertyChanged(nameof(HasNote));
        }
    }

    string _kind = "";
    public string Kind => _kind;

    bool _isEditable;
    public bool IsEditable
    {
        get => _isEditable;
        private set
        {
            if (!SetField(ref _isEditable, value)) return;
            ResetCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
            PasteAllCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ShowCartesianFields => Kind != "joint";
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    double _x, _y, _z, _a, _b, _c;
    public double X { get => _x; set => SetField(ref _x, value); }
    public double Y { get => _y; set => SetField(ref _y, value); }
    public double Z { get => _z; set => SetField(ref _z, value); }
    public double A { get => _a; set => SetField(ref _a, value); }
    public double B { get => _b; set => SetField(ref _b, value); }
    public double C { get => _c; set => SetField(ref _c, value); }

    string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (!SetField(ref _statusMessage, value)) return;
            OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    internal Action<int>? OnWaypointIndexChanged { get; set; }
    internal Action<string>? OnSequenceReloadRequested { get; set; }

    public void Open(ToolChangeWaypoint wp, int index, string sequenceId)
    {
        _waypoint   = wp;
        _sequenceId = sequenceId;
        _waypointIndex = index;
        OnWaypointIndexChanged?.Invoke(index);

        Title = wp.Name.Equals("HOME", StringComparison.OrdinalIgnoreCase) ? "HOME" : wp.Name;
        _kind = wp.Kind;

        if (wp.Kind == "joint")
        {
            Subtitle = "Joint-space home pose";
            OnPropertyChanged(nameof(ShowCartesianFields));
            Note = "Home is defined in $config.dat as joint angles — not editable here.";
            OnPropertyChanged(nameof(HasNote));
            IsEditable = false;
            IsOpen = true;
            return;
        }

        var move = wp.Move == KrlMoveKind.Lin ? "LIN" : "PTP";
        var editable = KrlGlobalPointsEditor.IsEditableNamedPoint(wp.Name, wp.Kind);
        IsEditable = editable;
        Subtitle = editable
            ? $"{move} · {wp.Name} → Global_Points.dat"
            : $"{move} · inline point";
        Note = editable
            ? ""
            : wp.Kind == "unresolved"
                ? "Point not found in Global_Points.dat — cannot edit."
                : "Inline literal inside the .src — copy works, but edit it in the program file.";

        _original = new KrlGlobalPointsEditor.PointPose(wp.X, wp.Y, wp.Z, wp.A, wp.B, wp.C);
        X = wp.X; Y = wp.Y; Z = wp.Z; A = wp.A; B = wp.B; C = wp.C;

        OnPropertyChanged(nameof(ShowCartesianFields));
        OnPropertyChanged(nameof(HasNote));
        StatusMessage = "";
        OnPropertyChanged(nameof(HasStatusMessage));
        IsOpen = true;
        PasteAllCommand.RaiseCanExecuteChanged();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _waypoint = null;
        _sequenceId = null;
        _waypointIndex = -1;
        StatusMessage = "";
        OnWaypointIndexChanged?.Invoke(-1);
    }

    void Reset()
    {
        X = _original.X; Y = _original.Y; Z = _original.Z;
        A = _original.A; B = _original.B; C = _original.C;
        StatusMessage = "";
    }

    void CopyAll()
    {
        _copiedPose = new KrlGlobalPointsEditor.PointPose((float)X, (float)Y, (float)Z, (float)A, (float)B, (float)C);
        StatusMessage = $"Copied X={Fmt(_copiedPose.X)} Y={Fmt(_copiedPose.Y)} Z={Fmt(_copiedPose.Z)}";
        PasteAllCommand.RaiseCanExecuteChanged();
    }

    void PasteAll()
    {
        if (_copiedPose is not { } p) return;
        X = p.X; Y = p.Y; Z = p.Z; A = p.A; B = p.B; C = p.C;
        StatusMessage = "Pasted — review, then Save changes";
    }

    void Save()
    {
        if (_waypoint is null || _sequenceId is null || !IsEditable) return;

        var pose = new KrlGlobalPointsEditor.PointPose((float)X, (float)Y, (float)Z, (float)A, (float)B, (float)C);
        foreach (var (label, v) in new[] { ("X", pose.X), ("Y", pose.Y), ("Z", pose.Z),
                                           ("A", pose.A), ("B", pose.B), ("C", pose.C) })
        {
            if (!float.IsFinite(v))
            {
                StatusMessage = $"Invalid {label} value";
                return;
            }
        }

        try
        {
            var result = KrlGlobalPointsEditor.UpdatePoint(_waypoint.Name, pose);
            if (result.Unchanged)
            {
                StatusMessage = "No changes to save";
                return;
            }

            StatusMessage = $"Saved {result.PointName} → {result.FileName}";
            OnSequenceReloadRequested?.Invoke(_sequenceId);
            Close();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}