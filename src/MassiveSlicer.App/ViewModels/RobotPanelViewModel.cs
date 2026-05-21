using System.Collections.ObjectModel;
using System.Numerics;
using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.FK;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Manages the ROBOT settings sub-tab: six joint sliders (A1–A6) for the
/// KR 210 R3100 Ultra (LFAM 2), plus TCP readout. Angles are in KRL degrees.
/// Limits are LFAM 2 software limits; defaults are the LFAM 2 home position.
/// </summary>
public sealed class RobotPanelViewModel : ViewModelBase
{
    // ── Joint limits (driven by cell config) ────────────────────────────────

    private double _minA1 = -360, _maxA1 = 360;
    private double _minA2 = -360, _maxA2 = 360;
    private double _minA3 = -360, _maxA3 = 360;
    private double _minA4 = -360, _maxA4 = 360;
    private double _minA5 = -360, _maxA5 = 360;
    private double _minA6 = -360, _maxA6 = 360;

    public double MinA1 { get => _minA1; set => SetField(ref _minA1, value); }
    public double MaxA1 { get => _maxA1; set => SetField(ref _maxA1, value); }
    public double MinA2 { get => _minA2; set => SetField(ref _minA2, value); }
    public double MaxA2 { get => _maxA2; set => SetField(ref _maxA2, value); }
    public double MinA3 { get => _minA3; set => SetField(ref _minA3, value); }
    public double MaxA3 { get => _maxA3; set => SetField(ref _maxA3, value); }
    public double MinA4 { get => _minA4; set => SetField(ref _minA4, value); }
    public double MaxA4 { get => _maxA4; set => SetField(ref _maxA4, value); }
    public double MinA5 { get => _minA5; set => SetField(ref _minA5, value); }
    public double MaxA5 { get => _maxA5; set => SetField(ref _maxA5, value); }
    public double MinA6 { get => _minA6; set => SetField(ref _minA6, value); }
    public double MaxA6 { get => _maxA6; set => SetField(ref _maxA6, value); }

    // ── Joint angles (KRL degrees) ───────────────────────────────────────────

    private double _a1 =   0;
    private double _a2 = -90;
    private double _a3 =  90;
    private double _a4 =   0;
    private double _a5 =  15;
    private double _a6 =   0;

    public double A1 { get => _a1; set => SetField(ref _a1, Math.Clamp(value, _minA1, _maxA1)); }
    public double A2 { get => _a2; set => SetField(ref _a2, Math.Clamp(value, _minA2, _maxA2)); }
    public double A3 { get => _a3; set => SetField(ref _a3, Math.Clamp(value, _minA3, _maxA3)); }
    public double A4 { get => _a4; set => SetField(ref _a4, Math.Clamp(value, _minA4, _maxA4)); }
    public double A5 { get => _a5; set => SetField(ref _a5, Math.Clamp(value, _minA5, _maxA5)); }
    public double A6 { get => _a6; set => SetField(ref _a6, Math.Clamp(value, _minA6, _maxA6)); }

    /// <summary>Applies joint limits and home position from the loaded cell config.</summary>
    public void Configure(IReadOnlyList<JointConfig> joints, float[] home)
    {
        if (joints.Count >= 6)
        {
            MinA1 = joints[0].MinDeg; MaxA1 = joints[0].MaxDeg;
            MinA2 = joints[1].MinDeg; MaxA2 = joints[1].MaxDeg;
            MinA3 = joints[2].MinDeg; MaxA3 = joints[2].MaxDeg;
            MinA4 = joints[3].MinDeg; MaxA4 = joints[3].MaxDeg;
            MinA5 = joints[4].MinDeg; MaxA5 = joints[4].MaxDeg;
            MinA6 = joints[5].MinDeg; MaxA6 = joints[5].MaxDeg;
        }
        if (home.Length >= 6)
        {
            A1 = home[0]; A2 = home[1]; A3 = home[2];
            A4 = home[3]; A5 = home[4]; A6 = home[5];
        }
    }

    // ── TCP readout ───────────────────────────────────────────────────────────

    private double _tcpX, _tcpY, _tcpZ;
    private double _tcpA, _tcpB, _tcpC;

    /// <summary>TCP X position in mm (Z-up world frame).</summary>
    public double TcpX { get => _tcpX; set => SetField(ref _tcpX, value); }
    /// <summary>TCP Y position in mm.</summary>
    public double TcpY { get => _tcpY; set => SetField(ref _tcpY, value); }
    /// <summary>TCP Z position in mm.</summary>
    public double TcpZ { get => _tcpZ; set => SetField(ref _tcpZ, value); }
    /// <summary>TCP A rotation (Euler Z) in degrees — flange orientation in ROBROOT.</summary>
    public double TcpA { get => _tcpA; set => SetField(ref _tcpA, value); }
    /// <summary>TCP B rotation (Euler Y) in degrees.</summary>
    public double TcpB { get => _tcpB; set => SetField(ref _tcpB, value); }
    /// <summary>TCP C rotation (Euler X) in degrees.</summary>
    public double TcpC { get => _tcpC; set => SetField(ref _tcpC, value); }

    // ── Flange readout (ROBROOT frame, from scene graph) ─────────────────────

    private double _flangeX, _flangeY, _flangeZ;

    /// <summary>Flange X position in ROBROOT frame (mm) — from scene graph FK.</summary>
    public double FlangeX { get => _flangeX; set => SetField(ref _flangeX, value); }
    /// <summary>Flange Y position in ROBROOT frame (mm) — from scene graph FK.</summary>
    public double FlangeY { get => _flangeY; set => SetField(ref _flangeY, value); }
    /// <summary>Flange Z position in ROBROOT frame (mm) — from scene graph FK.</summary>
    public double FlangeZ { get => _flangeZ; set => SetField(ref _flangeZ, value); }

    // ── Solver FK readout (ROBROOT frame, from solver FK) ────────────────────
    // Updated by GoToBedCenter to show what the solver thinks the position is.
    // Should match FlangeX/Y/Z; a mismatch reveals an FK discrepancy.

    private double _solverFkX, _solverFkY, _solverFkZ;

    /// <summary>Solver FK flange X in ROBROOT frame (mm) — from IK solver's internal FK.</summary>
    public double SolverFkX { get => _solverFkX; set => SetField(ref _solverFkX, value); }
    /// <summary>Solver FK flange Y in ROBROOT frame (mm).</summary>
    public double SolverFkY { get => _solverFkY; set => SetField(ref _solverFkY, value); }
    /// <summary>Solver FK flange Z in ROBROOT frame (mm).</summary>
    public double SolverFkZ { get => _solverFkZ; set => SetField(ref _solverFkZ, value); }

    // ── IK target orientation ────────────────────────────────────────────────
    // Orientation held fixed while dragging the TCP marker in the viewport.

    private double _ikTargetA = 0;
    private double _ikTargetB = 0;
    private double _ikTargetC = 0;

    /// <summary>Target TCP A (Euler Z) in degrees for IK drag.</summary>
    public double IkTargetA { get => _ikTargetA; set => SetField(ref _ikTargetA, value); }
    /// <summary>Target TCP B (Euler Y) in degrees for IK drag.</summary>
    public double IkTargetB { get => _ikTargetB; set => SetField(ref _ikTargetB, value); }
    /// <summary>Target TCP C (Euler X) in degrees for IK drag.</summary>
    public double IkTargetC { get => _ikTargetC; set => SetField(ref _ikTargetC, value); }

    // ── Tool selection ────────────────────────────────────────────────────────

    private IReadOnlyList<ToolCellConfig> _toolLibrary = [];
    private int _selectedToolIndex = 0;

    /// <summary>Display names for the available tool heads.</summary>
    public ObservableCollection<string> ToolNames { get; } = [];

    /// <summary>Index of the currently selected tool in <see cref="ToolNames"/>.</summary>
    public int SelectedToolIndex
    {
        get => _selectedToolIndex;
        set
        {
            if (!SetField(ref _selectedToolIndex, value)) return;
            if ((uint)value < (uint)_toolLibrary.Count)
                OnToolSelected?.Invoke(_toolLibrary[value]);
        }
    }

    /// <summary>
    /// Callback invoked when the user selects a different tool.
    /// Wired by <c>ViewportView</c> to trigger the async load + swap.
    /// </summary>
    internal Action<ToolCellConfig>? OnToolSelected { get; set; }

    /// <summary>Populates the tool selector from the cell's effective tool list.</summary>
    public void SetToolLibrary(IReadOnlyList<ToolCellConfig> tools)
    {
        _toolLibrary = tools;
        ToolNames.Clear();
        foreach (var t in tools)
            ToolNames.Add(string.IsNullOrEmpty(t.Name) ? t.ModelPath : t.Name);
        _selectedToolIndex = 0;
        OnPropertyChanged(nameof(SelectedToolIndex));
    }

    // ── IK ────────────────────────────────────────────────────────────────────

    private Vector3 _bedCenterRobot;
    private Vector3 _tcpOffsetLocal;
    private Vector3 _robotWorldPos;

    /// <summary>
    /// GLTF-based numerical IK solver. Set by <c>ViewportView</c> once the robot
    /// model and cell config are both loaded.
    /// </summary>
    internal GltfNumericalIkSolver? IkSolver { get; set; }

    /// <summary>
    /// Supplies bed-center position and TCP offset so <see cref="GoToBedCenterCommand"/> can run.
    /// <paramref name="bedCenterRobot"/> is the nozzle target in ROBROOT frame (mm).
    /// <paramref name="robotWorldPos"/> is the robot base in world/scene frame (mm) — used for the TCP readout.
    /// </summary>
    public void SetIkData(Vector3 bedCenterRobot, Vector3 tcpOffset, Vector3 robotWorldPos)
    {
        _bedCenterRobot = bedCenterRobot;
        _tcpOffsetLocal = tcpOffset;
        _robotWorldPos  = robotWorldPos;
    }

    public ICommand GoToBedCenterCommand { get; } = new RelayCommand(() => { });

    public RobotPanelViewModel()
    {
        GoToBedCenterCommand = new RelayCommand(GoToBedCenter);
    }

    private void GoToBedCenter()
    {
        if (IkSolver is null) return;

        var target = new OpenTK.Mathematics.Vector3(_bedCenterRobot.X, _bedCenterRobot.Y, _bedCenterRobot.Z);
        var seed   = new float[] { (float)A1, (float)A2, (float)A3, (float)A4, (float)A5, (float)A6 };
        var rot    = IkSolver.TargetRotFromKukaAbc(0f, 90f, 0f);
        var result = IkSolver.Solve(target, seed, rot);
        if (result is null) return;

        var solverTcp = IkSolver.ComputeTcpPosScene(result);

        SolverFkX = Math.Round(solverTcp.X - _robotWorldPos.X, 1);
        SolverFkY = Math.Round(solverTcp.Y - _robotWorldPos.Y, 1);
        SolverFkZ = Math.Round(solverTcp.Z - _robotWorldPos.Z, 1);

        A1 = Math.Round(result[0], 2);
        A2 = Math.Round(result[1], 2);
        A3 = Math.Round(result[2], 2);
        A4 = Math.Round(result[3], 2);
        A5 = Math.Round(result[4], 2);
        A6 = Math.Round(result[5], 2);
    }
}
