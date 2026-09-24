using System.Text.Json;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Detects per-robot recipes disappearing from the Lab, and keeps this PC's last copy of
/// them so they can be restored.
/// </summary>
/// <remarks>
/// A slicer build from before per-robot recipes publishes only the shared recipe, which
/// wipes every robot's entry on the Lab. Detection compares against the robots this PC last
/// SAW on the Lab, not against the local file: a robot that was only saved locally and never
/// published was never on the Lab, so its absence there is not a loss.
/// Lost robots are parked here until someone restores them or the Lab gets its own entry
/// back. Nothing restores them automatically: this copy may be older than what the team
/// wants. Lives in AppData, not the repo, so it never shows up in git.
/// </remarks>
public static class KrlPostProcessSectionBackup
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MassiveSlicer", "krl_postprocess_missing_sections.json");

    /// <summary>State file location. Tests point this at a temp file.</summary>
    public static string FilePath { get; set; } = DefaultPath;

    private sealed class State
    {
        /// <summary>Robots with an entry in the last Lab document this PC pulled or published.</summary>
        public List<string> SeenOnLab { get; set; } = [];

        /// <summary>This PC's last copy of robots that vanished from the Lab.</summary>
        public Dictionary<string, KrlPostProcessSettings> Parked { get; set; } = [];
    }

    /// <summary>Parked robot recipes (this PC's copies of robots missing from the Lab).</summary>
    public static Dictionary<string, KrlPostProcessSettings> Load() => Read().Parked;

    /// <summary>
    /// Call with each Lab document this PC pulls, BEFORE it overwrites the local file.
    /// Robots seen on the Lab last time but absent now are parked (from
    /// <paramref name="local"/>); parked robots the Lab has again are dropped.
    /// Returns the robots currently missing from the Lab.
    /// </summary>
    public static IReadOnlyList<string> Reconcile(KrlPostProcessSettings lab, KrlPostProcessSettings local)
    {
        var state = Read();
        foreach (var name in state.SeenOnLab)
        {
            if (KrlPostProcessCells.Find(lab, name) is not null) continue;
            if (KrlPostProcessCells.Find(local, name) is { } copy)
                state.Parked[name] = copy;
        }
        return Commit(state, lab);
    }

    /// <summary>Call after this PC successfully publishes <paramref name="lab"/>.</summary>
    public static IReadOnlyList<string> Published(KrlPostProcessSettings lab) => Commit(Read(), lab);

    /// <summary>Parked robots <paramref name="lab"/> still lacks.</summary>
    public static IReadOnlyList<string> StillMissing(KrlPostProcessSettings lab)
        => Sorted(Read().Parked.Keys.Where(n => KrlPostProcessCells.Find(lab, n) is null));

    private static IReadOnlyList<string> Commit(State state, KrlPostProcessSettings lab)
    {
        foreach (var name in state.Parked.Keys.ToList())
            if (KrlPostProcessCells.Find(lab, name) is not null)
                state.Parked.Remove(name);
        state.SeenOnLab = KrlPostProcessCells.Names(lab).ToList();
        Write(state);
        return Sorted(state.Parked.Keys);
    }

    private static IReadOnlyList<string> Sorted(IEnumerable<string> names)
    {
        var list = names.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static State Read()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath), KrlPostProcessDocument.JsonOptions)
                       ?? new State();
        }
        catch { /* unreadable state = nothing seen, nothing parked */ }
        return new State();
    }

    private static void Write(State state)
    {
        if (state.SeenOnLab.Count == 0 && state.Parked.Count == 0)
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(state, KrlPostProcessDocument.JsonOptions));
    }
}
