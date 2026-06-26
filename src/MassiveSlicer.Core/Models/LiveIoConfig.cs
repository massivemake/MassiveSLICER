namespace MassiveSlicer.Core.Models;

public enum LiveIoSignalKind
{
    DigitalInput,
    DigitalOutput,
    AnalogInput,
    AnalogOutput,
}

public enum LiveIoHighlight
{
    Normal,
    Safety,
    Fault,
}

/// <summary>Where a signal is polled — mirrors MassiveCONNECT monitor.py sources.</summary>
public enum LiveIoSource
{
    /// <summary>KUKA KRC4 via C3Bridge (e.g. <c>$IN[6]</c>).</summary>
    Kuka,
    /// <summary>Extruder RevPi CARACOL DIO via lfam-monitor bridge TCP:8765.</summary>
    ExtruderBridge,
    /// <summary>Extruder Pos28 expansion DIO (LFAM 3 valve cabinet) via bridge <c>io28</c>.</summary>
    ExtruderIo28,
    /// <summary>Extruder analog/MIO + zone temps via bridge <c>analog</c> + Modbus <c>hr_30xxx</c>.</summary>
    ExtruderModbus,
    /// <summary>Milling cabinet RevPi DIO via lfam-monitor bridge on <c>millIp:8765</c>.</summary>
    MillingModbus,
}

/// <summary>Display scale for analog channels.</summary>
public enum LiveIoValueFormat
{
    Raw,
    TempC,
    RpmPercent,
    Millivolt,
}

/// <summary>One monitored I/O point. <see cref="Key"/> is source-specific (KRL var, bridge pin, or modbus reg).</summary>
public sealed record LiveIoSignalConfig(
    string Label,
    string Key,
    LiveIoSignalKind Kind,
    LiveIoSource Source = LiveIoSource.Kuka,
    LiveIoHighlight Highlight = LiveIoHighlight.Normal,
    string? Unit = null,
    bool Writable = false,
    LiveIoValueFormat ValueFormat = LiveIoValueFormat.Raw);

/// <summary>Grouped signals for one machine subsystem (robot, extruder, spindle).</summary>
public sealed record LiveIoSectionConfig(
    string Title,
    IReadOnlyList<LiveIoSignalConfig> Signals);

/// <summary>Live I/O monitor layout for a cell. Optional in cell JSON — LFAM 3 has a built-in default.</summary>
public sealed record LiveIoConfig(IReadOnlyList<LiveIoSectionConfig> Sections);