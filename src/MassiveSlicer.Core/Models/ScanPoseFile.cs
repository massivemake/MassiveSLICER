namespace MassiveSlicer.Core.Models;

/// <summary>
/// Named joint poses for the scan tab. Stored in <c>*.scanposes.json</c> beside the
/// cell, not in the cell JSON and not in a workspace. Recalling one moves the
/// simulated robot only.
/// </summary>
public sealed class ScanPoseFile
{
    /// <summary>Name shown as selected. Missing or unknown names fall back to <see cref="ScanPoses.OverviewName"/>.</summary>
    public string? Selected { get; set; }

    public List<ScanPoseConfig> Poses { get; set; } = [];
}

/// <summary>One saved scan pose. Angles are A1–A6 and optional E1, in KRL degrees.</summary>
public sealed record ScanPoseConfig
{
    public required string Name { get; init; }

    public float[] Angles { get; init; } = [];
}

/// <summary>Built-in scan poses and the rules for merging a saved sidecar over them.</summary>
public static class ScanPoses
{
    public const string OverviewName = "Overview";

    /// <summary>
    /// Scanner aimed down over the bed centre. Same joints as the cell waypoint
    /// <c>scanner-down-bed</c>. Kept here so a cell-JSON copy cannot drop the pose.
    /// </summary>
    public static readonly float[] OverviewAngles = [1.66f, -66.75f, 109.43f, -180.19f, -46.19f, 197f, 0f];

    /// <summary>
    /// Returns a catalog that always contains Overview. Saved poses replace a
    /// built-in of the same name and are appended otherwise. Angles shorter than
    /// A1–A6 are dropped.
    /// </summary>
    public static ScanPoseFile EnsureOverview(ScanPoseFile? saved)
    {
        var poses = new List<ScanPoseConfig>
        {
            new() { Name = OverviewName, Angles = CopyAngles(OverviewAngles) },
        };

        if (saved?.Poses is not null)
        {
            foreach (var pose in saved.Poses)
            {
                if (pose is null || string.IsNullOrWhiteSpace(pose.Name)) continue;
                var angles = CopyAngles(pose.Angles);
                if (angles.Length < 6) continue;
                var name = pose.Name.Trim();
                int idx = poses.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    poses[idx] = new ScanPoseConfig { Name = poses[idx].Name, Angles = angles };
                else
                    poses.Add(new ScanPoseConfig { Name = name, Angles = angles });
            }
        }

        string selected = OverviewName;
        if (!string.IsNullOrWhiteSpace(saved?.Selected))
        {
            var want = saved.Selected.Trim();
            var hit = poses.FirstOrDefault(p => p.Name.Equals(want, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) selected = hit.Name;
        }

        return new ScanPoseFile { Selected = selected, Poses = poses };
    }

    /// <summary>A1–A6, plus E1 when the source has a seventh value. Empty when shorter than six.</summary>
    public static float[] CopyAngles(float[]? angles)
    {
        if (angles is not { Length: >= 6 }) return [];
        if (angles.Length >= 7)
            return [angles[0], angles[1], angles[2], angles[3], angles[4], angles[5], angles[6]];
        return [angles[0], angles[1], angles[2], angles[3], angles[4], angles[5]];
    }
}
