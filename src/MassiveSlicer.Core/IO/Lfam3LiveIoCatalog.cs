using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// LFAM 3 live I/O map sourced from MassiveCONNECT
/// (<c>reference/MassiveCONNECT-V2/MassiveCONNECT/monitor.py</c> and
/// <c>lfam_src/modbus_monitor_gui_original.py</c>).
/// </summary>
public static class Lfam3LiveIoCatalog
{
    public static LiveIoConfig Default { get; } = new(
    [
        RobotSection(),
        ScanSection(),
        ExtruderSection(),
        MillingSection(),
    ]);

    static LiveIoSectionConfig RobotSection() => new("Robot (KUKA)",
    [
        // Digital inputs — tool changer / flange interlocks (MassiveCONNECT KUKA panel)
        new("Extruder ready",      "$IN[6]",  LiveIoSignalKind.DigitalInput,  LiveIoSource.Kuka, LiveIoHighlight.Normal),
        new("Extruder dock occ.",  "$IN[10]", LiveIoSignalKind.DigitalInput,  LiveIoSource.Kuka),
        new("Spindle dock occ.",   "$IN[11]", LiveIoSignalKind.DigitalInput,  LiveIoSource.Kuka),
        new("Flange unlocked",     "$IN[12]", LiveIoSignalKind.DigitalInput,  LiveIoSource.Kuka, LiveIoHighlight.Safety),
        new("Flange locked",       "$IN[13]", LiveIoSignalKind.DigitalInput,  LiveIoSource.Kuka, LiveIoHighlight.Safety),
        new("Tool mounted",        "$IN[14]", LiveIoSignalKind.DigitalInput,  LiveIoSource.Kuka),
        new("Pressure OK",         "$IN[15]", LiveIoSignalKind.DigitalInput,  LiveIoSource.Kuka, LiveIoHighlight.Safety),
        new("Scanner dock occ.",   "$IN[17]", LiveIoSignalKind.DigitalInput,  LiveIoSource.Kuka),
        new("Flange detached",     "$IN[7]",  LiveIoSignalKind.DigitalInput,  LiveIoSource.Kuka, LiveIoHighlight.Safety),

        // Digital outputs — writable via C3Bridge Message #1
        new("Air / cooling",       "$OUT[5]",  LiveIoSignalKind.DigitalOutput, LiveIoSource.Kuka, Writable: true),
        new("Print enable",        "$OUT[7]",  LiveIoSignalKind.DigitalOutput, LiveIoSource.Kuka, Writable: true),
        new("MIO request",         "$OUT[9]",  LiveIoSignalKind.DigitalOutput, LiveIoSource.Kuka, Writable: true),
        new("Gripper close",       "$OUT[11]", LiveIoSignalKind.DigitalOutput, LiveIoSource.Kuka, Writable: true),
        new("Gripper open",        "$OUT[12]", LiveIoSignalKind.DigitalOutput, LiveIoSource.Kuka, Writable: true),
        new("EV flange",           "$OUT[13]", LiveIoSignalKind.DigitalOutput, LiveIoSource.Kuka, Writable: true),
        new("EV extruder",         "$OUT[14]", LiveIoSignalKind.DigitalOutput, LiveIoSource.Kuka, Writable: true),
        new("EV spindle",          "$OUT[15]", LiveIoSignalKind.DigitalOutput, LiveIoSource.Kuka, Writable: true),
        new("Tool-change enable",  "$OUT[16]", LiveIoSignalKind.DigitalOutput, LiveIoSource.Kuka, Writable: true),

        // Analog outputs — zone heater + extruder RPM commands
        new("Zone 1 temp cmd",     "$ANOUT[1]", LiveIoSignalKind.AnalogOutput, LiveIoSource.Kuka, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),
        new("Zone 2 temp cmd",     "$ANOUT[2]", LiveIoSignalKind.AnalogOutput, LiveIoSource.Kuka, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),
        new("Zone 3 temp cmd",     "$ANOUT[3]", LiveIoSignalKind.AnalogOutput, LiveIoSource.Kuka, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),
        new("Extruder RPM cmd",    "$ANOUT[4]", LiveIoSignalKind.AnalogOutput, LiveIoSource.Kuka, Unit: "%",   ValueFormat: LiveIoValueFormat.RpmPercent),
    ]);

    static LiveIoSectionConfig ScanSection() => new("Scanner",
    [
        // LFAM 3 Pos28 valve cabinet — scanner / tool pneumatics
        new("Solenoid A (LOCK)",   "O_1", LiveIoSignalKind.DigitalOutput, LiveIoSource.ExtruderIo28, Writable: true),
        new("Solenoid B (UNLOCK)", "O_5", LiveIoSignalKind.DigitalOutput, LiveIoSource.ExtruderIo28, Writable: true),
        // Bed-scan program status (Phase 2 bridge)
        new("Scan program ready",  "DI_scanReady",     LiveIoSignalKind.DigitalInput,  LiveIoSource.ExtruderBridge),
        new("Capture in progress", "DI_captureActive", LiveIoSignalKind.DigitalInput,  LiveIoSource.ExtruderBridge),
    ]);

    static LiveIoSectionConfig ExtruderSection() => new("Pellet Extruder",
    [
        // CARACOL safety / status (RevPi bridge port 8765)
        new("Safety gate",         "DI_02_safetyGate",            LiveIoSignalKind.DigitalInput,  LiveIoSource.ExtruderBridge, LiveIoHighlight.Safety),
        new("Emg robot",           "DI_03_emergencyRobot",        LiveIoSignalKind.DigitalInput,  LiveIoSource.ExtruderBridge, LiveIoHighlight.Fault),
        new("Emg box",             "DI_11_emergencyBox",          LiveIoSignalKind.DigitalInput,  LiveIoSource.ExtruderBridge, LiveIoHighlight.Fault),
        new("Emg HMI",             "DI_12_emergencyHMI",          LiveIoSignalKind.DigitalInput,  LiveIoSource.ExtruderBridge, LiveIoHighlight.Fault),
        new("Emg controller",      "DI_14_emergencyExtrController",LiveIoSignalKind.DigitalInput, LiveIoSource.ExtruderBridge, LiveIoHighlight.Fault),
        new("Motor contactor",     "DI_06_motorContactorState",   LiveIoSignalKind.DigitalInput,  LiveIoSource.ExtruderBridge),
        new("Heaters contactor",   "DI_07_heatersContactorState", LiveIoSignalKind.DigitalInput,  LiveIoSource.ExtruderBridge),
        new("ACK button",          "DI_10_ackButton",             LiveIoSignalKind.DigitalInput,  LiveIoSource.ExtruderBridge),

        new("Door lock",           "DO_01_doorLock_cmd",          LiveIoSignalKind.DigitalOutput, LiveIoSource.ExtruderBridge, Writable: true),
        new("Motor enable",        "DO_05_motorEnable",           LiveIoSignalKind.DigitalOutput, LiveIoSource.ExtruderBridge, Writable: true),
        new("Extruder → KUKA",     "DO_06_extruderReady",         LiveIoSignalKind.DigitalOutput, LiveIoSource.ExtruderBridge, Writable: true),
        new("Blower",              "DO_09_blower",                LiveIoSignalKind.DigitalOutput, LiveIoSource.ExtruderBridge, Writable: true),
        new("Green lamp",          "DO_12_greenLamp",             LiveIoSignalKind.DigitalOutput, LiveIoSource.ExtruderBridge),
        new("Orange lamp",         "DO_13_orangeLamp",            LiveIoSignalKind.DigitalOutput, LiveIoSource.ExtruderBridge),
        new("Red lamp",            "DO_14_redLamp",               LiveIoSignalKind.DigitalOutput, LiveIoSource.ExtruderBridge),

        // Zone temperatures — Modbus holding registers (actual / setpoint)
        new("Gearbox actual",      "hr_30200", LiveIoSignalKind.AnalogInput,  LiveIoSource.ExtruderModbus, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),
        new("Zone 1 actual",        "hr_30201", LiveIoSignalKind.AnalogInput,  LiveIoSource.ExtruderModbus, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),
        new("Zone 1 setpoint",      "hr_30101", LiveIoSignalKind.AnalogOutput, LiveIoSource.ExtruderModbus, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),
        new("Zone 2 actual",        "hr_30202", LiveIoSignalKind.AnalogInput,  LiveIoSource.ExtruderModbus, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),
        new("Zone 2 setpoint",      "hr_30102", LiveIoSignalKind.AnalogOutput, LiveIoSource.ExtruderModbus, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),
        new("Zone 3 actual",        "hr_30203", LiveIoSignalKind.AnalogInput,  LiveIoSource.ExtruderModbus, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),
        new("Zone 3 setpoint",      "hr_30103", LiveIoSignalKind.AnalogOutput, LiveIoSource.ExtruderModbus, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),

        // MIO analog — bridge analog dict
        new("Motor velocity",      "AI_09_MIO_extruderMotorVel", LiveIoSignalKind.AnalogInput, LiveIoSource.ExtruderBridge, Unit: "V", ValueFormat: LiveIoValueFormat.Millivolt),
        new("Motor HLFB",          "AI_01_MIO_HLFB_motorVel",    LiveIoSignalKind.AnalogInput, LiveIoSource.ExtruderBridge, Unit: "V", ValueFormat: LiveIoValueFormat.Millivolt),
        new("Nozzle RTD 1",        "RTDValue_1",                 LiveIoSignalKind.AnalogInput, LiveIoSource.ExtruderBridge, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),
        new("Nozzle RTD 2",        "RTDValue_2",                 LiveIoSignalKind.AnalogInput, LiveIoSource.ExtruderBridge, Unit: "°C", ValueFormat: LiveIoValueFormat.TempC),
    ]);

    static LiveIoSectionConfig MillingSection() => new("Milling Spindle",
    [
        // Milling cabinet RevPi — lfam-monitor bridge on millIp:8765 (LFAM3 only)
        new("Gate open stop",      "DI_04_gateOpenStop",    LiveIoSignalKind.DigitalInput,  LiveIoSource.MillingModbus, LiveIoHighlight.Safety),
        new("SS1 standstill",      "DI_05_SS1standstill",   LiveIoSignalKind.DigitalInput,  LiveIoSource.MillingModbus, LiveIoHighlight.Safety),
        new("SS1 stop",            "DI_06_SS1stop",         LiveIoSignalKind.DigitalInput,  LiveIoSource.MillingModbus, LiveIoHighlight.Safety),
        new("Emergency state",     "DI_07_emergencyState",  LiveIoSignalKind.DigitalInput,  LiveIoSource.MillingModbus, LiveIoHighlight.Fault),
        new("KUKA running →",      "DI_08_digitalFromKUKA", LiveIoSignalKind.DigitalInput,  LiveIoSource.MillingModbus),
        new("Red lamp",            "DO_01_redLamp",         LiveIoSignalKind.DigitalOutput, LiveIoSource.MillingModbus),
        new("Yellow lamp",         "DO_02_yellowLamp",      LiveIoSignalKind.DigitalOutput, LiveIoSource.MillingModbus),
        new("Green lamp",          "DO_03_greenLamp",       LiveIoSignalKind.DigitalOutput, LiveIoSource.MillingModbus),
    ]);

    /// <summary>RevPi DIO names polled for the LFAM 3 milling cabinet bridge.</summary>
    public static IReadOnlyList<string> MillingPollKeys { get; } =
    [
        "DI_04_gateOpenStop", "DI_05_SS1standstill", "DI_06_SS1stop",
        "DI_07_emergencyState", "DI_08_digitalFromKUKA",
        "DO_01_redLamp", "DO_02_yellowLamp", "DO_03_greenLamp",
    ];

    /// <summary>Flat list of all KUKA variables MassiveCONNECT batch-reads for LFAM3.</summary>
    public static IReadOnlyList<string> KukaPollVariables { get; } =
    [
        "$IN[6]", "$IN[7]", "$IN[10]", "$IN[11]", "$IN[12]", "$IN[13]", "$IN[14]", "$IN[15]", "$IN[17]",
        "$OUT[5]", "$OUT[7]", "$OUT[9]", "$OUT[11]", "$OUT[12]", "$OUT[13]", "$OUT[14]", "$OUT[15]", "$OUT[16]",
        "$ANOUT[1]", "$ANOUT[2]", "$ANOUT[3]", "$ANOUT[4]",
    ];
}