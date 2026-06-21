using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>Implementation status for the LFAM 3 live I/O monitor rollout.</summary>
public enum LiveIoPhaseStatus
{
    Implemented,
    Pending,
}

/// <summary>One rollout phase — scope, data source, and evaluation checklist.</summary>
public sealed record LiveIoPhaseDefinition(
    int Number,
    string Title,
    LiveIoPhaseStatus Status,
    LiveIoSource PrimarySource,
    string ConnectionSummary,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> ImplementationTasks);

/// <summary>
/// Rollout plan for the collapsible live I/O panel.
/// Phases 1–3 are implemented.
/// Reference: MassiveCONNECT <c>monitor.py</c>.
/// </summary>
public static class LiveIoPhasePlan
{
    public static LiveIoPhaseDefinition Phase1 { get; } = new(
        Number: 1,
        Title: "KUKA Robot",
        Status: LiveIoPhaseStatus.Implemented,
        PrimarySource: LiveIoSource.Kuka,
        ConnectionSummary: "C3Bridge on cell BridgeIp:BridgePort (sync robot)",
        AcceptanceCriteria:
        [
            "Open LFAM 3 → Show Live I/O → Sync Robot: Robot column shows Live · C3Bridge",
            "$IN[6–17] digital inputs update ~2×/s with lime/amber/red indicators",
            "$OUT[5–16] digital outputs show HIGH/LOW; ⇅ force writes with Confirm",
            "$ANOUT[1–4] show °C / RPM % using LFAM scaling",
            "Closing panel or desyncing stops C3Bridge I/O polling (axes still stream)",
        ],
        ImplementationTasks: []);

    public static LiveIoPhaseDefinition Phase2 { get; } = new(
        Number: 2,
        Title: "Pellet Extruder",
        Status: LiveIoPhaseStatus.Implemented,
        PrimarySource: LiveIoSource.ExtruderBridge,
        ConnectionSummary: "lfam-monitor JSON bridge TCP:8765 on extIp (Pos30·Pos28·AIO·MIO + Modbus)",
        AcceptanceCriteria:
        [
            "CARACOL DI/DO (Pos30) live: safety gate, emergencies, motor/heater contactors, lamps",
            "Pos28 valve DIO live on LFAM 3: O_1 / O_5 with confirmed force writes",
            "Modbus zone temps: hr_301xx setpoint vs hr_302xx actual",
            "MIO analog: motor velocity, RTD nozzle temps",
            "Extruder section status shows Live · bridge when ext_ip reachable",
        ],
        ImplementationTasks: []);

    public static LiveIoPhaseDefinition Phase3 { get; } = new(
        Number: 3,
        Title: "Milling Spindle",
        Status: LiveIoPhaseStatus.Implemented,
        PrimarySource: LiveIoSource.MillingModbus,
        ConnectionSummary: "lfam-monitor JSON bridge TCP:8765 on millIp (LFAM3: 192.168.0.249)",
        AcceptanceCriteria:
        [
            "Milling DI live: gate, SS1, emergency, KUKA-running signal",
            "Status lamps (red/yellow/green) reflect milling cabinet state",
            "Milling section status shows P3 live · bridge when millIp reachable",
        ],
        ImplementationTasks: []);

    public static IReadOnlyList<LiveIoPhaseDefinition> All { get; } = [Phase1, Phase2, Phase3];

    public static LiveIoPhaseDefinition? ForSection(string sectionTitle) => sectionTitle switch
    {
        "Robot (KUKA)"     => Phase1,
        "Scanner"          => Phase2,
        "Pellet Extruder"  => Phase2,
        "Milling Spindle"  => Phase3,
        _                  => null,
    };

    public static string RoadmapSummary =>
        string.Join(" · ", All.Select(p =>
            p.Status == LiveIoPhaseStatus.Implemented
                ? $"P{p.Number} live"
                : $"P{p.Number} pending"));
}