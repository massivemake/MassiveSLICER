using System.Text.Json;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Result of checking whether MassiveDRIVE is free for CELL-based calibration
/// (bed-cal / scan-cal must not run while an RSI path is active).
/// </summary>
public sealed record MassiveDrivePathStatus(
    bool Reachable,
    bool PathActive,
    string? Phase,
    string? LastAction,
    string? JobId,
    string? Detail)
{
    /// <summary>True when MassiveDRIVE is reachable and no path is running.</summary>
    public bool SafeForCalibration => Reachable && !PathActive;

    public string Summary
    {
        get
        {
            if (!Reachable)
                return string.IsNullOrWhiteSpace(Detail)
                    ? "MassiveDRIVE unreachable"
                    : $"MassiveDRIVE unreachable: {Detail}";
            if (PathActive)
            {
                var bits = new List<string> { "path executor ACTIVE" };
                if (!string.IsNullOrWhiteSpace(Phase)) bits.Add($"phase={Phase}");
                if (!string.IsNullOrWhiteSpace(LastAction)) bits.Add(LastAction!);
                if (!string.IsNullOrWhiteSpace(JobId)) bits.Add($"job={JobId}");
                return string.Join(" · ", bits);
            }
            return "path executor idle";
        }
    }
}

/// <summary>
/// Parses MassiveDRIVE <c>/api/executor</c> (or live.executor) JSON into a path-busy flag.
/// </summary>
public static class MassiveDrivePathGate
{
    /// <summary>
    /// Reads <paramref name="root"/> from <c>/api/executor</c> or an <c>executor</c> object
    /// nested under <c>/api/live</c>.
    /// </summary>
    public static MassiveDrivePathStatus ParseExecutorJson(JsonElement root)
    {
        var exec = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("executor", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            exec = nested;
        }

        bool active = ReadBool(exec, "active");
        string? phase = ReadString(exec, "phase");
        string? last = ReadString(exec, "last_action");
        string? jobId = ReadString(exec, "job_id") ?? ReadString(exec, "package_id");

        return new MassiveDrivePathStatus(
            Reachable: true,
            PathActive: active,
            Phase: phase,
            LastAction: last,
            JobId: jobId,
            Detail: null);
    }

    public static MassiveDrivePathStatus Unreachable(string detail) =>
        new(false, false, null, null, null, detail);

    static bool ReadBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p))
            return false;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => p.TryGetInt32(out var n) && n != 0,
            JsonValueKind.String => bool.TryParse(p.GetString(), out var b) && b
                                   || string.Equals(p.GetString(), "1", StringComparison.Ordinal),
            _ => false,
        };
    }

    static string? ReadString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString()
            : p.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                ? p.GetRawText()
                : null;
    }
}
